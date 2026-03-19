using System;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Data;

namespace PrinterServices.Workers
{
    /// <summary>
    /// Worker que purga registros de logs con más de 30 días de antigüedad.
    /// Se ejecuta al inicio y luego cada hora para mantener el tamaño de la BD acotado.
    /// Tablas purgadas: print_jobs, print_log, notifications, printer_status_log,
    /// network_alerts, print_latency_log, network_latency_baseline,
    /// job_status_callbacks, notificacionescambiosip.
    /// Tablas NO purgadas (datos maestros/config): printers, network_current,
    /// config_settings, printer_latency_stats, network_snapshots.
    /// </summary>
    public class DbMaintenanceWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(DbMaintenanceWorker));

        private const int RETENTION_DAYS = 30;
        private const int INTERVAL_HOURS = 1;

        private readonly PrinterServiceDb _db;

        private Thread _workerThread;
        private CancellationTokenSource _cts;
        private bool _isRunning;

        public DbMaintenanceWorker(PrinterServiceDb db)
        {
            _db = db;
            _isRunning = false;
        }

        public void Start()
        {
            if (_isRunning)
            {
                Log.Warn("[DB-MAINTENANCE] Worker ya está ejecutándose");
                return;
            }

            Log.Info("[DB-MAINTENANCE] Iniciando DbMaintenanceWorker...");

            _cts = new CancellationTokenSource();

            _workerThread = new Thread(() => WorkerLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "DbMaintenanceWorker"
            };

            _isRunning = true;
            _workerThread.Start();

            Log.Info("[DB-MAINTENANCE] DbMaintenanceWorker iniciado (retención: " + RETENTION_DAYS + " días, intervalo: " + INTERVAL_HOURS + "h)");
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                Log.Warn("[DB-MAINTENANCE] Worker ya está detenido");
                return;
            }

            Log.Info("[DB-MAINTENANCE] Deteniendo DbMaintenanceWorker...");

            _cts?.Cancel();

            if (_workerThread != null && _workerThread.IsAlive)
            {
                bool joined = _workerThread.Join(TimeSpan.FromSeconds(5));
                if (!joined)
                {
                    Log.Warn("[DB-MAINTENANCE] Worker no se detuvo en 5s");
                }
            }

            _isRunning = false;
            Log.Info("[DB-MAINTENANCE] DbMaintenanceWorker detenido");
        }

        private void WorkerLoop(CancellationToken cancellationToken)
        {
            // Ejecutar inmediatamente al inicio del servicio
            try
            {
                PurgeOldRecords();
            }
            catch (Exception ex)
            {
                Log.Error("[DB-MAINTENANCE] Error en purga inicial", ex);
            }

            // Luego repetir cada hora
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Task.Delay(TimeSpan.FromHours(INTERVAL_HOURS), cancellationToken).Wait();
                }
                catch (OperationCanceledException)
                {
                    Log.Info("[DB-MAINTENANCE] Cancelación solicitada durante espera");
                    break;
                }
                catch (AggregateException ae) when (ae.InnerException is OperationCanceledException)
                {
                    Log.Info("[DB-MAINTENANCE] Cancelación solicitada durante espera");
                    break;
                }

                try
                {
                    PurgeOldRecords();
                }
                catch (Exception ex)
                {
                    Log.Error("[DB-MAINTENANCE] Error en purga periódica", ex);
                }
            }

            Log.Info("[DB-MAINTENANCE] Worker loop finalizado");
        }

        /// <summary>
        /// Purga registros con más de RETENTION_DAYS días de antigüedad en todas las tablas de logs.
        /// Cada tabla se purga individualmente con try/catch para que un error en una no bloquee las demás.
        /// Al finalizar, ejecuta VACUUM para liberar espacio en disco.
        /// </summary>
        private void PurgeOldRecords()
        {
            string cutoff = DateTime.Now.AddDays(-RETENTION_DAYS).ToString("yyyy-MM-ddTHH:mm:ss");
            Log.InfoFormat("[DB-MAINTENANCE] Purgando registros anteriores a {0}...", cutoff);

            int totalDeleted = 0;

            // Tablas con columna de fecha tipo string ISO 8601
            totalDeleted += PurgeTable("print_jobs", "fecha_creacion", cutoff);
            totalDeleted += PurgeTable("print_log", "fecha", cutoff);
            totalDeleted += PurgeTable("notifications", "fecha", cutoff);
            totalDeleted += PurgeTable("printer_status_log", "fecha", cutoff);

            // Tablas con columna de fecha tipo DateTime (SQLite-net las almacena como string ISO 8601)
            totalDeleted += PurgeTable("network_alerts", "detected_at", cutoff);
            totalDeleted += PurgeTable("print_latency_log", "created_at", cutoff);
            totalDeleted += PurgeTable("network_latency_baseline", "checked_at", cutoff);
            totalDeleted += PurgeTable("job_status_callbacks", "fecha_creacion", cutoff);
            totalDeleted += PurgeTable("notificacionescambiosip", "fecha_creacion", cutoff);

            if (totalDeleted > 0)
            {
                // VACUUM para liberar espacio en disco después de purgar
                try
                {
                    _db.Execute("VACUUM");
                    Log.InfoFormat("[DB-MAINTENANCE] Purga completada: {0} registro(s) eliminados + VACUUM ejecutado", totalDeleted);
                }
                catch (Exception ex)
                {
                    Log.WarnFormat("[DB-MAINTENANCE] Purga completada ({0} eliminados) pero VACUUM falló: {1}",
                        totalDeleted, ex.Message);
                }
            }
            else
            {
                Log.Info("[DB-MAINTENANCE] Sin registros antiguos que purgar");
            }
        }

        /// <summary>
        /// Elimina registros de una tabla donde la columna de fecha es anterior al cutoff.
        /// </summary>
        /// <param name="tableName">Nombre de la tabla SQLite</param>
        /// <param name="dateColumn">Nombre de la columna de fecha</param>
        /// <param name="cutoff">Fecha de corte en formato ISO 8601 (yyyy-MM-ddTHH:mm:ss)</param>
        /// <returns>Cantidad de registros eliminados</returns>
        private int PurgeTable(string tableName, string dateColumn, string cutoff)
        {
            try
            {
                int count = _db.Execute(
                    string.Format("DELETE FROM {0} WHERE {1} < ?", tableName, dateColumn),
                    cutoff);

                if (count > 0)
                {
                    Log.InfoFormat("[DB-MAINTENANCE] {0}: {1} registro(s) eliminados", tableName, count);
                }

                return count;
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[DB-MAINTENANCE] Error purgando {0}: {1}", tableName, ex.Message);
                return 0;
            }
        }

        public bool IsRunning()
        {
            return _isRunning;
        }
    }
}
