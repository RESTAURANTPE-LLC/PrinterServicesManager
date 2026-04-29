using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Queue
{
    public class PrintJobManager
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrintJobManager));

        private readonly ConcurrentQueue<PrintJob> _queue;
        private readonly SemaphoreSlim _signal;
        private readonly PrinterServiceDb _db;
        private readonly object _statsLock = new object();

        // ── Guard: Estado en RAM como fuente de verdad (thread-safe) ──
        private readonly ConcurrentDictionary<string, PrintJobStatus> _jobStates
            = new ConcurrentDictionary<string, PrintJobStatus>();
        private readonly ConcurrentDictionary<string, object> _jobLocks
            = new ConcurrentDictionary<string, object>();

        private int _totalEnqueued;
        private int _totalProcessed;

        // Contador de jobs que están imprimiendo (PRINTING) por impresora.
        // RAZÓN: el probe de capacidades corre en el mismo tick del StatusMonitor y
        // NO debe ejecutarse si hay un job imprimiendo o en cola para esa impresora,
        // porque competiría por el port lock que el PrintWorker necesita enseguida.
        // Contar en memoria es O(1), mucho más barato que consultar la BD cada tick.
        private readonly ConcurrentDictionary<string, int> _jobsImprimiendoPorImpresora
            = new ConcurrentDictionary<string, int>();

        // Evento que se dispara cada vez que un job entra a la cola.
        // El ProbeScheduler lo escucha para cancelar un probe en curso sobre la misma
        // impresora y liberar el port lock al PrintWorker de inmediato.
        // El argumento string es el ImpresoraId.
        public event EventHandler<string> JobEncolado;

        public int PendingCount { get { return _queue.Count; } }
        public int TotalEnqueued { get { return _totalEnqueued; } }
        public int TotalProcessed { get { return _totalProcessed; } }

        /// <summary>
        /// ¿Tiene esta impresora al menos un job pendiente en cola o imprimiendo?
        /// Se usa como "freno" antes de correr el probe: si está ocupada, salteamos.
        /// </summary>
        public bool TieneJobsEnColaOImprimiendo(string impresoraId)
        {
            if (string.IsNullOrEmpty(impresoraId)) return false;

            // Primero el contador atómico de jobs imprimiendo (barato).
            int imprimiendo;
            if (_jobsImprimiendoPorImpresora.TryGetValue(impresoraId, out imprimiendo) && imprimiendo > 0)
                return true;

            // Después la cola pendiente (iteración snapshot consistente).
            foreach (var j in _queue)
            {
                if (j != null && string.Equals(j.ImpresoraId, impresoraId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Un job empezó a imprimir: suma 1 al contador de la impresora.</summary>
        internal void MarcarJobImprimiendo(string impresoraId)
        {
            if (string.IsNullOrEmpty(impresoraId)) return;
            _jobsImprimiendoPorImpresora.AddOrUpdate(impresoraId, 1, (_, v) => v + 1);
        }

        /// <summary>Un job dejó de imprimir (DONE/FAILED/WAITING): resta 1. Nunca baja de 0.</summary>
        internal void QuitarJobImprimiendo(string impresoraId)
        {
            if (string.IsNullOrEmpty(impresoraId)) return;
            _jobsImprimiendoPorImpresora.AddOrUpdate(impresoraId, 0, (_, v) => Math.Max(0, v - 1));
        }

        public PrintJobManager(PrinterServiceDb db)
        {
            _db = db;
            _queue = new ConcurrentQueue<PrintJob>();
            _signal = new SemaphoreSlim(0);
        }

        /// <summary>
        /// Avisa al ProbeScheduler que un job acaba de entrar a la cola para esta
        /// impresora. Si un listener lanza excepción, la tragamos — no debe afectar
        /// el encolado, que es el camino crítico.
        /// </summary>
        private void NotificarJobEncolado(string impresoraId)
        {
            var handler = JobEncolado;
            if (handler == null || string.IsNullOrEmpty(impresoraId)) return;
            try { handler(this, impresoraId); }
            catch (Exception ex)
            {
                Log.Warn("[QUEUE] Listener de JobEncolado falló (ignorado): " + ex.Message);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Guard: Transiciones atómicas de estado
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Verifica si un estado es terminal (DONE, FAILED, EXPIRED) — inmutable.</summary>
        public static bool IsTerminalState(PrintJobStatus status)
        {
            return status == PrintJobStatus.Done || status == PrintJobStatus.Failed || status == PrintJobStatus.Expired;
        }

        /// <summary>Consulta estado real en RAM. Retorna null si el job no existe.</summary>
        public PrintJobStatus? GetStateInMemory(string jobId)
        {
            PrintJobStatus state;
            return _jobStates.TryGetValue(jobId, out state) ? state : (PrintJobStatus?)null;
        }

        /// <summary>
        /// Transición atómica de estado con lock por job.
        /// Estados terminales (DONE, FAILED, EXPIRED) NUNCA cambian.
        /// </summary>
        public bool TryChangeState(string jobId, PrintJobStatus? expectedState, PrintJobStatus newState)
        {
            var jobLock = _jobLocks.GetOrAdd(jobId, _ => new object());
            lock (jobLock)
            {
                PrintJobStatus currentState;
                if (!_jobStates.TryGetValue(jobId, out currentState))
                    return false; // Job no existe en RAM

                // Estados terminales son inmutables
                if (IsTerminalState(currentState))
                {
                    Log.WarnFormat("[GUARD] Job {0} en estado terminal {1} — transición a {2} RECHAZADA",
                        jobId, currentState, newState);
                    return false;
                }

                // Validar estado esperado si se especificó
                if (expectedState.HasValue && currentState != expectedState.Value)
                {
                    Log.WarnFormat("[GUARD] Job {0} estado actual={1}, esperado={2} — transición a {3} RECHAZADA",
                        jobId, currentState, expectedState.Value, newState);
                    return false;
                }

                _jobStates[jobId] = newState;
                return true;
            }
        }

        /// <summary>Transición atómica: PENDING → PRINTING. Retorna false si ya no es PENDING.</summary>
        public bool MarkPrinting(PrintJob job)
        {
            if (!TryChangeState(job.JobId, PrintJobStatus.Pending, PrintJobStatus.Printing))
                return false;

            job.Estado = PrintJobStatus.Printing;
            UpdateJobInDb(job);
            MarcarJobImprimiendo(job.ImpresoraId);
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        // Guard: Hash anti-duplicación
        // ═══════════════════════════════════════════════════════════════

        /// <summary>SHA256 del contenido completo del job para detección de duplicados.</summary>
        public static string CalculateContentHash(PrintJob job)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(job.Contenido ?? "");
            sb.Append(job.ContenidoHtml ?? "");
            sb.Append(job.ImpresoraId ?? "");
            sb.Append(job.ComandaId ?? "");
            sb.Append(job.PedidoIds != null ? string.Join(",", job.PedidoIds) : "");
            sb.Append(job.LineasImprimirJson ?? "");
            sb.Append(job.DocumentoJson ?? "");

            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Registra hash ANTES de enviar bytes a impresora. INSERT OR REPLACE.</summary>
        public void RegisterPrintHash(PrintJob job, string hash)
        {
            try
            {
                var entity = new PrintedJobHashEntity
                {
                    JobId = job.JobId,
                    ImpresoraId = job.ImpresoraId ?? "",
                    ContenidoHash = hash,
                    FechaImpresion = DateTime.Now.ToString("o"),
                    ImpresoraNombre = job.ImpresoraNombre,
                    AreaImpresion = job.AreaImpresion,
                    TipoImpresion = job.TipoImpresion,
                    PedidoIds = job.PedidoIds != null ? string.Join(",", job.PedidoIds) : null,
                    ComandaId = job.ComandaId
                };
                _db.InsertOrReplace(entity);
            }
            catch (Exception ex)
            {
                Log.Error("[GUARD] Error registrando hash para job " + job.JobId + ": " + ex.Message);
            }
        }

        /// <summary>¿Este job ya fue impreso físicamente? Consulta por job_id en printed_jobs_hash.</summary>
        public bool IsAlreadyPrinted(string jobId)
        {
            try
            {
                var count = _db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM printed_jobs_hash WHERE job_id = ?", jobId);
                return count > 0;
            }
            catch { return false; }
        }

        /// <summary>Purga hashes con más de 24h. Llamar periódicamente desde ExpirationLoop.</summary>
        public void PurgeOldHashes()
        {
            try
            {
                int deleted = _db.Execute(
                    "DELETE FROM printed_jobs_hash WHERE fecha_impresion < ?",
                    DateTime.Now.AddHours(-24).ToString("o"));
                if (deleted > 0)
                    Log.InfoFormat("[GUARD] Purgados {0} hashes antiguos (>24h)", deleted);
            }
            catch (Exception ex)
            {
                Log.Warn("[GUARD] Error purgando hashes: " + ex.Message);
            }
        }

        public string Enqueue(PrintJob job)
        {
            if (job == null) throw new ArgumentNullException("job");

            // Persistir en SQLite primero
            try
            {
                var entity = job.ToEntity();
                _db.Insert(entity);
                Log.DebugFormat("[QUEUE] Job {0} persistido en SQLite", job.JobId);
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error al persistir job en SQLite: " + ex.Message, ex);
            }

            // Guard: Registrar estado en RAM (fuente de verdad)
            _jobStates[job.JobId] = job.Estado;

            _queue.Enqueue(job);
            Interlocked.Increment(ref _totalEnqueued);
            _signal.Release();
            NotificarJobEncolado(job.ImpresoraId);

            Log.InfoFormat("[QUEUE] Job {0} encolado → impresora={1} ip={2} tipo={3} area={4}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, job.ImpresoraIp, job.TipoImpresion, job.AreaImpresion ?? "-");

            return job.JobId;
        }

        public async Task<PrintJob> DequeueAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct);

            PrintJob job;
            if (_queue.TryDequeue(out job))
            {
                Interlocked.Increment(ref _totalProcessed);
                return job;
            }

            return null;
        }

        public void Retry(PrintJob job)
        {
            if (job == null) return;

            // Guard: No reintentar estados terminales (usar RetryManual para eso)
            var ramState = GetStateInMemory(job.JobId);
            if (ramState.HasValue && IsTerminalState(ramState.Value))
            {
                Log.WarnFormat("[GUARD] Job {0} en estado terminal {1} — Retry automático rechazado", job.JobId, ramState.Value);
                return;
            }

            string estadoPrevio = job.Estado.ToString().ToUpper();
            _jobStates[job.JobId] = PrintJobStatus.Pending;

            job.Reintentos++;
            job.Estado = PrintJobStatus.Pending;
            job.ErrorMensaje = null;
            job.FechaCreacion = DateTime.Now;

            UpdateJobInDb(job);
            LogRetry(job, estadoPrevio);

            _queue.Enqueue(job);
            _signal.Release();
            NotificarJobEncolado(job.ImpresoraId);

            Log.InfoFormat("[QUEUE] Job {0} re-encolado desde {1} (reintento {2}/{3})",
                job.JobId, estadoPrevio, job.Reintentos, job.MaxReintentos);
        }

        public bool MarkDone(PrintJob job)
        {
            // Guard: Transición atómica PRINTING → DONE
            if (!TryChangeState(job.JobId, PrintJobStatus.Printing, PrintJobStatus.Done))
            {
                // Forzar si no existía en RAM (job recuperado de BD)
                _jobStates[job.JobId] = PrintJobStatus.Done;
            }

            job.Estado = PrintJobStatus.Done;
            job.FechaImpresion = DateTime.Now;
            UpdateJobInDb(job);
            QuitarJobImprimiendo(job.ImpresoraId);

            Log.InfoFormat("[QUEUE] Job {0} completado → impresora={1}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId);
            return true;
        }

        public bool MarkFailed(PrintJob job, string error)
        {
            // Guard: No cambiar estados terminales
            var ramState = GetStateInMemory(job.JobId);
            if (ramState.HasValue && IsTerminalState(ramState.Value))
            {
                Log.WarnFormat("[GUARD] Job {0} ya es {1} — MarkFailed rechazado", job.JobId, ramState.Value);
                return false;
            }
            bool estabaImprimiendo = ramState.HasValue && ramState.Value == PrintJobStatus.Printing;
            _jobStates[job.JobId] = PrintJobStatus.Failed;

            job.Estado = PrintJobStatus.Failed;
            job.ErrorMensaje = error;
            UpdateJobInDb(job);
            if (estabaImprimiendo) QuitarJobImprimiendo(job.ImpresoraId);

            Log.WarnFormat("[QUEUE] Job {0} FALLIDO → {1}", job.JobId, error);
            return true;
        }

        public bool MarkWaiting(PrintJob job, string reason)
        {
            var ramState = GetStateInMemory(job.JobId);
            if (ramState.HasValue && IsTerminalState(ramState.Value))
            {
                Log.WarnFormat("[GUARD] Job {0} ya es {1} — MarkWaiting rechazado", job.JobId, ramState.Value);
                return false;
            }
            bool estabaImprimiendo = ramState.HasValue && ramState.Value == PrintJobStatus.Printing;
            _jobStates[job.JobId] = PrintJobStatus.Waiting;

            job.Estado = PrintJobStatus.Waiting;
            job.ErrorMensaje = reason;
            UpdateJobInDb(job);
            if (estabaImprimiendo) QuitarJobImprimiendo(job.ImpresoraId);

            Log.InfoFormat("[QUEUE] Job {0} en ESPERA → {1}", job.JobId, reason);
            return true;
        }

        public bool MarkExpired(PrintJob job, string reason)
        {
            if (!TryChangeState(job.JobId, PrintJobStatus.Waiting, PrintJobStatus.Expired))
            {
                Log.WarnFormat("[GUARD] Job {0} no está WAITING — MarkExpired rechazado", job.JobId);
                return false;
            }

            job.Estado = PrintJobStatus.Expired;
            job.ErrorMensaje = reason;
            UpdateJobInDb(job);
            // WAITING → EXPIRED: el job no estaba PRINTING, no hay in-flight que decrementar.

            Log.WarnFormat("[QUEUE] Job {0} EXPIRADO → {1}", job.JobId, reason);
            return true;
        }

        public bool ReEnqueue(PrintJob job)
        {
            if (job == null) return false;

            if (!TryChangeState(job.JobId, PrintJobStatus.Waiting, PrintJobStatus.Pending))
            {
                Log.WarnFormat("[GUARD] Job {0} no está WAITING — ReEnqueue rechazado", job.JobId);
                return false;
            }

            job.Estado = PrintJobStatus.Pending;
            job.ErrorMensaje = null;
            UpdateJobInDb(job);

            _queue.Enqueue(job);
            _signal.Release();
            NotificarJobEncolado(job.ImpresoraId);

            Log.InfoFormat("[QUEUE] Job {0} re-encolado desde WAITING → impresora={1}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId);
            return true;
        }

        public void RecoverPending()
        {
            try
            {
                // Guard: Recuperar PENDING + WAITING + PRINTING (PRINTING = posible crash post-impresión)
                var pending = _db.Query<PrintJobEntity>(
                    "SELECT * FROM print_jobs WHERE estado IN ('PENDING','WAITING','PRINTING') ORDER BY prioridad ASC, fecha_creacion ASC");

                if (pending == null || pending.Count == 0)
                {
                    Log.Info("[QUEUE] No hay jobs pendientes para recuperar");
                    return;
                }

                int recovered = 0;
                int skippedByHash = 0;

                foreach (var entity in pending)
                {
                    var job = PrintJob.FromEntity(entity);

                    // Guard: Verificar si este job ya fue impreso (hash anti-duplicación)
                    if (IsAlreadyPrinted(job.JobId))
                    {
                        // Ya impreso — marcar DONE sin reimprimir
                        job.Estado = PrintJobStatus.Done;
                        job.FechaImpresion = DateTime.Now;
                        UpdateJobInDb(job);
                        _jobStates[job.JobId] = PrintJobStatus.Done;
                        skippedByHash++;
                        Log.InfoFormat("[GUARD] Job {0} ya impreso (hash encontrado) — marcado DONE sin reimprimir", job.JobId);
                        continue;
                    }

                    // Si estaba PRINTING → volver a PENDING (no sabemos si se imprimió)
                    if (entity.Estado == "PRINTING")
                    {
                        job.Estado = PrintJobStatus.Pending;
                        UpdateJobInDb(job);
                    }

                    // Registrar en RAM y encolar
                    _jobStates[job.JobId] = job.Estado;
                    _queue.Enqueue(job);
                    _signal.Release();
                    NotificarJobEncolado(job.ImpresoraId);
                    recovered++;
                }

                Log.InfoFormat("[QUEUE] Recuperados {0} jobs, {1} omitidos por hash anti-duplicación", recovered, skippedByHash);
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error al recuperar jobs pendientes: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Retry manual desde API/dashboard. Permite salir de FAILED/EXPIRED (no de DONE).
        /// Elimina hash previo para permitir reimpresión.
        /// </summary>
        public void RetryManual(PrintJob job)
        {
            if (job == null) return;

            // DONE nunca se reintenta — ya se imprimió exitosamente
            var ramState = GetStateInMemory(job.JobId);
            if (ramState.HasValue && ramState.Value == PrintJobStatus.Done)
            {
                Log.WarnFormat("[GUARD] Job {0} está DONE — RetryManual rechazado", job.JobId);
                return;
            }

            // Eliminar hash previo para permitir reimpresión
            try
            {
                _db.Execute("DELETE FROM printed_jobs_hash WHERE job_id = ?", job.JobId);
            }
            catch { }

            // Forzar transición en RAM (bypass de TryChangeState para estados terminales)
            _jobStates[job.JobId] = PrintJobStatus.Pending;

            string estadoPrevio = job.Estado.ToString().ToUpper();
            job.Reintentos++;
            job.Estado = PrintJobStatus.Pending;
            job.ErrorMensaje = null;
            job.FechaCreacion = DateTime.Now;

            UpdateJobInDb(job);
            LogRetry(job, estadoPrevio);

            _queue.Enqueue(job);
            _signal.Release();
            NotificarJobEncolado(job.ImpresoraId);

            Log.InfoFormat("[QUEUE] Job {0} RetryManual desde {1} (reintento {2}/{3})",
                job.JobId, estadoPrevio, job.Reintentos, job.MaxReintentos);
        }

        private void LogRetry(PrintJob job, string estadoPrevio)
        {
            try
            {
                var logEntry = new PrintLogEntity
                {
                    JobId = job.JobId,
                    ImpresoraId = job.ImpresoraId,
                    ImpresoraNombre = job.ImpresoraNombre,
                    ImpresoraIp = job.ImpresoraIp,
                    Estado = "RETRIED",
                    Mensaje = string.Format("Reenviado desde estado {0} (reintento #{1})", estadoPrevio, job.Reintentos),
                    Reintentos = job.Reintentos,
                    Fecha = DateTime.Now.ToString("o"),
                    DeviceIdOrigen = job.DeviceIdOrigen,
                    AreaImpresion = job.AreaImpresion
                };
                _db.Insert(logEntry);
            }
            catch (Exception ex)
            {
                Log.Warn("[QUEUE] Error al registrar log de retry: " + ex.Message);
            }
        }

        public List<PrintJob> GetPendingJobs()
        {
            return _queue.ToArray().ToList();
        }

        public PrintJob GetJobById(string jobId)
        {
            // Primero buscar en cola
            var inQueue = _queue.ToArray().FirstOrDefault(j => j.JobId == jobId);
            if (inQueue != null) return inQueue;

            // Si no, buscar en SQLite
            try
            {
                var entity = _db.Query<PrintJobEntity>(
                    "SELECT * FROM print_jobs WHERE job_id = ?", jobId).FirstOrDefault();
                if (entity != null)
                {
                    return PrintJob.FromEntity(entity);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error buscando job " + jobId + ": " + ex.Message, ex);
            }

            return null;
        }

        /// <summary>
        /// Fase 23: Obtiene múltiples jobs por lista de IDs (consulta bulk).
        /// Busca primero en cola en memoria, luego en SQLite para los no encontrados.
        /// Usado por GET /api/jobs/status para que QuipuNetX consulte estados masivamente.
        /// </summary>
        /// <param name="jobIds">Lista de IDs de jobs a buscar</param>
        /// <returns>Lista de jobs encontrados (puede ser menor que jobIds si algunos no existen)</returns>
        public List<PrintJob> GetJobsByIds(List<string> jobIds)
        {
            if (jobIds == null || jobIds.Count == 0) return new List<PrintJob>(); // Validar entrada

            var result = new List<PrintJob>();                          // Lista resultado
            var queueSnapshot = _queue.ToArray();                      // Snapshot de la cola en memoria
            var notFoundInQueue = new List<string>();                   // IDs no encontrados en cola

            foreach (var jobId in jobIds)                              // Buscar cada ID en cola
            {
                var inQueue = queueSnapshot.FirstOrDefault(j => j.JobId == jobId); // Buscar en memoria
                if (inQueue != null)
                {
                    result.Add(inQueue);                               // Encontrado en cola
                }
                else
                {
                    notFoundInQueue.Add(jobId);                        // No está en cola, buscar en BD
                }
            }

            if (notFoundInQueue.Count > 0)                             // Si hay IDs no encontrados en cola
            {
                try
                {
                    // Construir query con IN (?) — SQLite no soporta arrays, usar string join
                    string inClause = string.Join(",", notFoundInQueue.Select(id => "'" + id.Replace("'", "''") + "'")); // Escapar comillas
                    string sql = "SELECT * FROM print_jobs WHERE job_id IN (" + inClause + ")"; // Query bulk
                    var entities = _db.Query<PrintJobEntity>(sql);     // Ejecutar query

                    foreach (var entity in entities)                    // Convertir entidades a jobs
                    {
                        result.Add(PrintJob.FromEntity(entity));       // Agregar al resultado
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[QUEUE] Error buscando jobs por IDs: " + ex.Message, ex); // Log de error
                }
            }

            return result;                                              // Retornar todos los encontrados
        }

        /// <summary>
        /// Elimina un job de SQLite (print_jobs + print_log) y de la cola en memoria.
        /// RAZÓN: Permite al operador limpiar jobs desde el dashboard.
        /// </summary>
        public bool DeleteJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) return false; // Validar que el jobId no sea vacío

            try
            {
                // Eliminar de SQLite: tabla print_jobs
                int deletedJobs = _db.Execute("DELETE FROM print_jobs WHERE job_id = ?", jobId); // Elimina el job principal
                // Eliminar de SQLite: tabla print_log (todos los registros de log de este job)
                int deletedLogs = _db.Execute("DELETE FROM print_log WHERE job_id = ?", jobId); // Elimina historial de log

                Log.InfoFormat("[QUEUE] Job {0} eliminado → {1} jobs, {2} logs borrados de SQLite",
                    jobId, deletedJobs, deletedLogs);

                return deletedJobs > 0 || deletedLogs > 0; // True si se eliminó algo
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error eliminando job " + jobId + ": " + ex.Message, ex);
                return false;
            }
        }

        private void UpdateJobInDb(PrintJob job)
        {
            try
            {
                var entity = job.ToEntity();
                _db.Update(entity);
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error actualizando job " + job.JobId + " en SQLite: " + ex.Message, ex);
            }
        }
    }
}
