using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;

namespace PrinterServices.Workers
{
    /// <summary>
    /// Worker que purga registros viejos para mantener el tamaño de la BD acotado.
    ///
    /// Dos niveles de purga:
    ///   • LIVIANO (default): borra registros más viejos que `RetencionNormalDias`
    ///     (default 30). Se ejecuta cada `IntervaloHoras` (default 1h al arrancar y
    ///     desde ahí cada hora — se mantiene por compatibilidad con el comportamiento
    ///     histórico). El transcript de comandos (print_job_attempt_commands) usa
    ///     su propia retención chica por ser de alto volumen.
    ///   • AGRESIVO: si el archivo .db supera `TamanioMaxMb` (default 200 MB), la
    ///     retención baja a `RetencionAgresivaDias` (default 7). Incluye las mismas
    ///     tablas del liviano, más print_jobs terminados. Al terminar, se re-mide el
    ///     tamaño — si sigue sobre el máximo, se loguea WARN (los jobs no terminales
    ///     nunca se borran, el operador tiene que investigar).
    ///
    /// Tablas NO purgadas nunca (datos maestros / config):
    ///   printers, network_current, config_settings, printer_latency_stats, network_snapshots.
    /// </summary>
    public class DbMaintenanceWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(DbMaintenanceWorker));

        // Fallbacks hardcoded si ConfigManager no está disponible o no tiene la key.
        private const int DEFAULT_RETENCION_NORMAL_DIAS = 30;
        private const int DEFAULT_RETENCION_AGRESIVA_DIAS = 7;
        private const int DEFAULT_TAMANIO_MAX_MB = 200;
        private const int DEFAULT_INTERVALO_HORAS = 1;
        private const int DEFAULT_TRANSCRIPT_RETENCION_DIAS = 7;
        private const int DEFAULT_VACUUM_THRESHOLD = 10000;

        private readonly PrinterServiceDb _db;

        private Thread _workerThread;
        private CancellationTokenSource _cts;
        private bool _isRunning;

        // Estado de la última purga (para que el dashboard muestre info).
        private DateTime? _ultimaPurga;
        private int _ultimaPurgaFilasEliminadas;
        private long _ultimaPurgaBytesLiberados;
        private string _ultimaPurgaModo; // "liviano" | "agresivo"
        private readonly object _estadoLock = new object();

        public DbMaintenanceWorker(PrinterServiceDb db)
        {
            _db = db;
            _isRunning = false;
        }

        public void Start()
        {
            if (_isRunning)
            {
                Log.Warn("[DB-MAINT] Worker ya está ejecutándose");
                return;
            }

            Log.Info("[DB-MAINT] Iniciando DbMaintenanceWorker...");

            _cts = new CancellationTokenSource();

            _workerThread = new Thread(() => WorkerLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "DbMaintenanceWorker"
            };

            _isRunning = true;
            _workerThread.Start();

            Log.InfoFormat("[DB-MAINT] Worker iniciado (retención normal: {0}d, agresiva: {1}d cuando BD > {2} MB, intervalo: {3}h)",
                LeerConfigInt("DatabaseRetentionNormalDays", DEFAULT_RETENCION_NORMAL_DIAS),
                LeerConfigInt("DatabasePurgeRetentionDays", DEFAULT_RETENCION_AGRESIVA_DIAS),
                LeerConfigInt("DatabaseMaxSizeMb", DEFAULT_TAMANIO_MAX_MB),
                LeerConfigInt("DatabaseMaintenanceIntervalHours", DEFAULT_INTERVALO_HORAS));
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                Log.Warn("[DB-MAINT] Worker ya está detenido");
                return;
            }

            Log.Info("[DB-MAINT] Deteniendo DbMaintenanceWorker...");

            _cts?.Cancel();

            if (_workerThread != null && _workerThread.IsAlive)
            {
                bool joined = _workerThread.Join(TimeSpan.FromSeconds(5));
                if (!joined)
                {
                    Log.Warn("[DB-MAINT] Worker no se detuvo en 5s");
                }
            }

            _isRunning = false;
            Log.Info("[DB-MAINT] Worker detenido");
        }

        /// <summary>
        /// Dispara una purga manual inmediata desde el dashboard. Retorna el resumen
        /// para mostrar en el response. Thread-safe (corre sincrónicamente en el
        /// thread del caller; el worker periódico sigue su ciclo sin interferencia).
        /// </summary>
        public ResumenPurga PurgarAhora()
        {
            try { return EjecutarPurga(); }
            catch (Exception ex)
            {
                Log.Error("[DB-MAINT] Error en purga manual", ex);
                return new ResumenPurga { Error = ex.Message };
            }
        }

        /// <summary>Snapshot del estado actual de la BD para el dashboard.</summary>
        public InfoMantenimiento ObtenerInfo()
        {
            var info = new InfoMantenimiento();
            info.TamanioMb = MedirTamanioDbMb();
            info.TamanioMaxMb = LeerConfigInt("DatabaseMaxSizeMb", DEFAULT_TAMANIO_MAX_MB);

            lock (_estadoLock)
            {
                info.UltimaPurga = _ultimaPurga;
                info.UltimaPurgaFilasEliminadas = _ultimaPurgaFilasEliminadas;
                info.UltimaPurgaBytesLiberados = _ultimaPurgaBytesLiberados;
                info.UltimaPurgaModo = _ultimaPurgaModo;
            }

            // Top tablas por filas (útil para debug cuando la BD crece).
            try
            {
                info.ConteoPorTabla = new System.Collections.Generic.List<ConteoTabla>();
                string[] tablas = {
                    "print_jobs", "print_log", "print_job_attempts", "print_job_attempt_commands",
                    "printer_status_log", "print_latency_log", "notifications",
                    "network_alerts", "devices_on_network", "network_speed_log",
                    "job_status_callbacks", "notificacionescambiosip", "printers"
                };
                foreach (var t in tablas)
                {
                    try
                    {
                        int n = _db.ExecuteScalar<int>("SELECT COUNT(*) FROM " + t);
                        info.ConteoPorTabla.Add(new ConteoTabla { Tabla = t, Filas = n });
                    }
                    catch { /* tabla puede no existir aún */ }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[DB-MAINT] No se pudo calcular conteos: " + ex.Message);
            }

            return info;
        }

        private void WorkerLoop(CancellationToken cancellationToken)
        {
            // Ejecutar inmediatamente al inicio del servicio.
            try { EjecutarPurga(); }
            catch (Exception ex) { Log.Error("[DB-MAINT] Error en purga inicial", ex); }

            // Luego repetir cada X horas según config.
            while (!cancellationToken.IsCancellationRequested)
            {
                int horas = LeerConfigInt("DatabaseMaintenanceIntervalHours", DEFAULT_INTERVALO_HORAS);
                if (horas < 1) horas = 1;

                try
                {
                    Task.Delay(TimeSpan.FromHours(horas), cancellationToken).Wait();
                }
                catch (OperationCanceledException) { break; }
                catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { break; }

                try { EjecutarPurga(); }
                catch (Exception ex) { Log.Error("[DB-MAINT] Error en purga periódica", ex); }
            }

            Log.Info("[DB-MAINT] Worker loop finalizado");
        }

        /// <summary>
        /// Lógica central de purga. Decide liviano vs agresivo según el tamaño actual
        /// de la BD, ejecuta, hace VACUUM, y mide nuevamente el tamaño para reportar.
        /// </summary>
        private ResumenPurga EjecutarPurga()
        {
            var resumen = new ResumenPurga();

            int retencionNormal = LeerConfigInt("DatabaseRetentionNormalDays", DEFAULT_RETENCION_NORMAL_DIAS);
            int retencionAgresiva = LeerConfigInt("DatabasePurgeRetentionDays", DEFAULT_RETENCION_AGRESIVA_DIAS);
            int tamanioMaxMb = LeerConfigInt("DatabaseMaxSizeMb", DEFAULT_TAMANIO_MAX_MB);
            int retencionTranscript = LeerConfigInt("CommandTranscriptRetentionDays", DEFAULT_TRANSCRIPT_RETENCION_DIAS);
            int vacuumThreshold = LeerConfigInt("DatabaseMaintenanceVacuumThreshold", DEFAULT_VACUUM_THRESHOLD);

            long tamanioAntes = MedirTamanioDbBytes();
            resumen.TamanioMbAntes = tamanioAntes / 1024.0 / 1024.0;
            bool modoAgresivo = resumen.TamanioMbAntes > tamanioMaxMb;
            resumen.Modo = modoAgresivo ? "agresivo" : "liviano";

            int diasRetencionEste = modoAgresivo ? retencionAgresiva : retencionNormal;
            string cutoffIso = DateTime.Now.AddDays(-diasRetencionEste).ToString("yyyy-MM-ddTHH:mm:ss");
            string cutoffTranscriptIso = DateTime.UtcNow.AddDays(-retencionTranscript).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            Log.InfoFormat("[DB-MAINT] Purga {0}: BD = {1:F1} MB (max={2}) → cutoff común = {3} ({4}d), cutoff transcript = {5} ({6}d)",
                resumen.Modo, resumen.TamanioMbAntes, tamanioMaxMb, cutoffIso, diasRetencionEste,
                cutoffTranscriptIso, retencionTranscript);

            int totalEliminados = 0;

            // Tablas con fecha tipo ISO 8601 estándar (del retention común).
            totalEliminados += PurgarTabla("print_log", "fecha", cutoffIso);
            totalEliminados += PurgarTabla("notifications", "fecha", cutoffIso);
            totalEliminados += PurgarTabla("printer_status_log", "fecha", cutoffIso);
            totalEliminados += PurgarTabla("network_alerts", "detected_at", cutoffIso);
            totalEliminados += PurgarTabla("print_latency_log", "created_at", cutoffIso);
            totalEliminados += PurgarTabla("network_latency_baseline", "checked_at", cutoffIso);
            totalEliminados += PurgarTabla("job_status_callbacks", "fecha_creacion", cutoffIso);
            totalEliminados += PurgarTabla("notificacionescambiosip", "fecha_creacion", cutoffIso);

            // Jobs: sólo borrar los terminales viejos. Un PENDING/WAITING/PRINTING nunca
            // se toca porque todavía puede estar esperando impresora o recuperación.
            totalEliminados += PurgarSql(
                "DELETE FROM print_jobs " +
                "WHERE fecha_creacion < ? AND estado IN ('DONE','FAILED','CANCELLED','EXPIRED')",
                "print_jobs (terminales)", cutoffIso);

            // Intentos de impresión: usan la misma retención de jobs (es el detalle
            // diagnóstico del job; sin el job no tiene sentido guardar los intentos).
            totalEliminados += PurgarTabla("print_job_attempts", "started_at", cutoffIso);

            // Transcript de comandos: retención propia y más chica (7d default), porque
            // es alto volumen (≥12 filas por intento × miles de intentos/día). Se guarda
            // en UTC por eso comparamos contra el cutoff UTC separado.
            totalEliminados += PurgarTabla("print_job_attempt_commands", "timestamp_utc", cutoffTranscriptIso);

            // Dispositivos descubiertos que ya no están online y no se vieron en mucho tiempo.
            totalEliminados += PurgarSql(
                "DELETE FROM devices_on_network WHERE last_seen_at < ? AND is_online = 0",
                "devices_on_network (offline viejos)", cutoffIso);

            // Historial de medición de velocidad (baja frecuencia, solo purge normal).
            totalEliminados += PurgarTabla("network_speed_log", "measured_at", cutoffIso);

            resumen.FilasEliminadas = totalEliminados;

            // VACUUM si se eliminó bastante — costoso, solo vale la pena con volumen.
            if (totalEliminados >= vacuumThreshold)
            {
                try
                {
                    _db.Execute("VACUUM");
                    resumen.VacuumEjecutado = true;
                    Log.InfoFormat("[DB-MAINT] VACUUM ejecutado tras {0} filas eliminadas", totalEliminados);
                }
                catch (Exception ex)
                {
                    Log.Warn("[DB-MAINT] VACUUM falló: " + ex.Message);
                }
            }
            else if (modoAgresivo && totalEliminados > 0)
            {
                // En modo agresivo siempre hacemos VACUUM (aunque sea bajo el threshold)
                // porque el objetivo es recuperar espacio en disco YA.
                try
                {
                    _db.Execute("VACUUM");
                    resumen.VacuumEjecutado = true;
                    Log.Info("[DB-MAINT] VACUUM forzado (modo agresivo)");
                }
                catch (Exception ex)
                {
                    Log.Warn("[DB-MAINT] VACUUM forzado falló: " + ex.Message);
                }
            }

            long tamanioDespues = MedirTamanioDbBytes();
            resumen.TamanioMbDespues = tamanioDespues / 1024.0 / 1024.0;
            resumen.BytesLiberados = Math.Max(0, tamanioAntes - tamanioDespues);

            // Warning si el modo agresivo no alcanzó para bajar del máximo.
            if (modoAgresivo && resumen.TamanioMbDespues > tamanioMaxMb)
            {
                Log.WarnFormat(
                    "[DB-MAINT] BD sigue por encima del máximo tras purga agresiva: {0:F1} MB > {1} MB. " +
                    "Revisar si hay tablas no cubiertas o jobs no terminales acumulados.",
                    resumen.TamanioMbDespues, tamanioMaxMb);
            }

            if (totalEliminados > 0)
            {
                Log.InfoFormat("[DB-MAINT] Purga {0} completada: {1} filas eliminadas, {2:F2} MB liberados ({3:F1} → {4:F1} MB)",
                    resumen.Modo, totalEliminados, resumen.BytesLiberados / 1024.0 / 1024.0,
                    resumen.TamanioMbAntes, resumen.TamanioMbDespues);
            }
            else
            {
                Log.Info("[DB-MAINT] Sin registros antiguos que purgar");
            }

            // Guardar estado para el dashboard.
            lock (_estadoLock)
            {
                _ultimaPurga = DateTime.Now;
                _ultimaPurgaFilasEliminadas = totalEliminados;
                _ultimaPurgaBytesLiberados = resumen.BytesLiberados;
                _ultimaPurgaModo = resumen.Modo;
            }

            return resumen;
        }

        private int PurgarTabla(string nombreTabla, string columnaFecha, string cutoff)
        {
            return PurgarSql(
                string.Format("DELETE FROM {0} WHERE {1} < ?", nombreTabla, columnaFecha),
                nombreTabla, cutoff);
        }

        private int PurgarSql(string sql, string etiqueta, string cutoff)
        {
            try
            {
                int n = _db.Execute(sql, cutoff);
                if (n > 0) Log.InfoFormat("[DB-MAINT] {0}: {1} filas eliminadas", etiqueta, n);
                return n;
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[DB-MAINT] Error purgando {0}: {1}", etiqueta, ex.Message);
                return 0;
            }
        }

        private long MedirTamanioDbBytes()
        {
            try
            {
                string path = _db.DatabasePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
                return new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                Log.Debug("[DB-MAINT] No se pudo medir tamaño: " + ex.Message);
                return 0;
            }
        }

        private double MedirTamanioDbMb()
        {
            return MedirTamanioDbBytes() / 1024.0 / 1024.0;
        }

        private int LeerConfigInt(string key, int defaultValue)
        {
            // ConfigManager se inicializa después de la BD pero antes de este worker.
            // Igual ponemos try por seguridad — si por alguna razón no está, usamos default.
            try { return ConfigManager.Instance.GetInt(key, defaultValue); }
            catch { return defaultValue; }
        }

        public bool IsRunning() { return _isRunning; }

        // ─────────────────────────────────────────────────────────────────────────
        // DTOs públicos (usados por DashboardController para responder al cliente)
        // ─────────────────────────────────────────────────────────────────────────

        public class ResumenPurga
        {
            public string Modo { get; set; }             // "liviano" | "agresivo"
            public int FilasEliminadas { get; set; }
            public long BytesLiberados { get; set; }
            public double TamanioMbAntes { get; set; }
            public double TamanioMbDespues { get; set; }
            public bool VacuumEjecutado { get; set; }
            public string Error { get; set; }
        }

        public class InfoMantenimiento
        {
            public double TamanioMb { get; set; }
            public int TamanioMaxMb { get; set; }
            public DateTime? UltimaPurga { get; set; }
            public int UltimaPurgaFilasEliminadas { get; set; }
            public long UltimaPurgaBytesLiberados { get; set; }
            public string UltimaPurgaModo { get; set; }
            public System.Collections.Generic.List<ConteoTabla> ConteoPorTabla { get; set; }
        }

        public class ConteoTabla
        {
            public string Tabla { get; set; }
            public int Filas { get; set; }
        }
    }
}
