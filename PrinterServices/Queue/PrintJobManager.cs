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

        private int _totalEnqueued;
        private int _totalProcessed;

        public int PendingCount { get { return _queue.Count; } }
        public int TotalEnqueued { get { return _totalEnqueued; } }
        public int TotalProcessed { get { return _totalProcessed; } }

        public PrintJobManager(PrinterServiceDb db)
        {
            _db = db;
            _queue = new ConcurrentQueue<PrintJob>();
            _signal = new SemaphoreSlim(0);
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

            _queue.Enqueue(job);
            Interlocked.Increment(ref _totalEnqueued);
            _signal.Release();

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

            string estadoPrevio = job.Estado.ToString().ToUpper();         // Guardar estado previo para print_log
            job.Reintentos++;
            job.Estado = PrintJobStatus.Pending;
            job.ErrorMensaje = null;
            job.FechaCreacion = DateTime.Now;                              // Resetear fecha para que ExpirationLoop
                                                                           // no lo expire inmediatamente si era EXPIRED

            UpdateJobInDb(job);

            // Registrar en print_log: evidencia de que un job EXPIRED/FAILED fue reenviado
            // Así queda: ... → EXPIRED (con hora original) → RETRIED (con hora nueva) → DONE/FAILED
            try
            {
                var logEntry = new Data.Models.PrintLogEntity
                {
                    JobId = job.JobId,
                    ImpresoraId = job.ImpresoraId,
                    ImpresoraNombre = job.ImpresoraNombre,
                    ImpresoraIp = job.ImpresoraIp,
                    Estado = "RETRIED",                                    // Estado descriptivo para el log
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
                Log.Warn("[QUEUE] Error al registrar log de retry: " + ex.Message); // No fatal
            }

            _queue.Enqueue(job);
            _signal.Release();

            Log.InfoFormat("[QUEUE] Job {0} re-encolado desde {1} (reintento {2}/{3})",
                job.JobId, estadoPrevio, job.Reintentos, job.MaxReintentos);
        }

        public void MarkDone(PrintJob job)
        {
            job.Estado = PrintJobStatus.Done;
            job.FechaImpresion = DateTime.Now;
            UpdateJobInDb(job);

            Log.InfoFormat("[QUEUE] Job {0} completado → impresora={1}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId);
        }

        public void MarkFailed(PrintJob job, string error)
        {
            job.Estado = PrintJobStatus.Failed;
            job.ErrorMensaje = error;
            UpdateJobInDb(job);

            Log.WarnFormat("[QUEUE] Job {0} FALLIDO → {1}", job.JobId, error);
        }

        public void MarkWaiting(PrintJob job, string reason)
        {
            job.Estado = PrintJobStatus.Waiting;
            job.ErrorMensaje = reason;
            UpdateJobInDb(job);

            Log.InfoFormat("[QUEUE] Job {0} en ESPERA → {1}", job.JobId, reason);
        }

        /// <summary>
        /// Fase 23: Marca un job como EXPIRED (superó tiempo máximo en WAITING).
        /// Es un estado terminal — el job NO se reintenta ni se re-encola.
        /// </summary>
        /// <param name="job">Job a marcar como expirado</param>
        /// <param name="reason">Motivo de expiración (ej: "Superó 300 segundos en WAITING")</param>
        public void MarkExpired(PrintJob job, string reason)
        {
            job.Estado = PrintJobStatus.Expired;   // Estado terminal: no se reintenta
            job.ErrorMensaje = reason;              // Guardar motivo de expiración
            UpdateJobInDb(job);                     // Persistir en BD

            Log.WarnFormat("[QUEUE] Job {0} EXPIRADO → {1}", job.JobId, reason); // Log de advertencia
        }

        public void ReEnqueue(PrintJob job)
        {
            if (job == null) return;

            job.Estado = PrintJobStatus.Pending;
            job.ErrorMensaje = null;
            UpdateJobInDb(job);

            _queue.Enqueue(job);
            _signal.Release();

            Log.InfoFormat("[QUEUE] Job {0} re-encolado desde WAITING → impresora={1}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId);
        }

        public void RecoverPending()
        {
            try
            {
                var pending = _db.Query<PrintJobEntity>(
                    "SELECT * FROM print_jobs WHERE estado IN ('PENDING','WAITING') ORDER BY prioridad ASC, fecha_creacion ASC");

                if (pending == null || pending.Count == 0)
                {
                    Log.Info("[QUEUE] No hay jobs pendientes para recuperar");
                    return;
                }

                foreach (var entity in pending)
                {
                    var job = PrintJob.FromEntity(entity);
                    _queue.Enqueue(job);
                    _signal.Release();
                }

                Log.InfoFormat("[QUEUE] Recuperados {0} jobs pendientes de SQLite", pending.Count);
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error al recuperar jobs pendientes: " + ex.Message, ex);
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
