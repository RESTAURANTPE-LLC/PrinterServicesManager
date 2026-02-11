using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Notifications;
using PrinterServices.Queue;

namespace PrinterServices.Monitoring
{
    public class StatusMonitor
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(StatusMonitor));

        private readonly PrinterServiceDb _db;
        private readonly PrintJobManager _jobManager;
        private readonly CancellationTokenSource _cts;

        public StatusMonitor(PrinterServiceDb db, PrintJobManager jobManager)
        {
            _db = db;
            _jobManager = jobManager;
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            var cfg = ConfigManager.Instance;
            Task.Factory.StartNew(() => MonitorLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Log.InfoFormat("[MONITOR] StatusMonitor iniciado (intervalo={0}s, timeout={1}ms)",
                cfg.GetInt("StatusCheckIntervalSeconds", 15),
                cfg.GetInt("TcpConnectTimeoutMs", 3000));
        }

        public void Stop()
        {
            _cts.Cancel();
            Log.Info("[MONITOR] StatusMonitor detenido");
        }

        private async Task MonitorLoop(CancellationToken ct)
        {
            // Espera inicial para que el servicio arranque
            await Task.Delay(5000, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await CheckAllPrintersAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("[MONITOR] Error en ciclo de monitoreo", ex);
                }

                try
                {
                    int intervalSeconds = ConfigManager.Instance.GetInt("StatusCheckIntervalSeconds", 15);
                    await Task.Delay(intervalSeconds * 1000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task CheckAllPrintersAsync(CancellationToken ct)
        {
            List<PrinterEntity> printers;
            try
            {
                printers = _db.Query<PrinterEntity>("SELECT * FROM printers WHERE ip IS NOT NULL AND ip != ''");
            }
            catch (Exception ex)
            {
                Log.Error("[MONITOR] Error consultando impresoras: " + ex.Message);
                return;
            }

            if (printers == null || printers.Count == 0) return;

            Log.DebugFormat("[MONITOR] Verificando {0} impresora(s)...", printers.Count);

            foreach (var printer in printers)
            {
                if (ct.IsCancellationRequested) break;

                bool wasOnline = printer.EstadoOnline == 1;

                try
                {
                    int checkTimeoutMs = ConfigManager.Instance.GetInt("TcpConnectTimeoutMs", 3000);
                    var status = await PrinterStatusChecker.CheckAsync(
                        printer.Ip, printer.Puerto, checkTimeoutMs, ct);

                    printer.EstadoOnline = status.Online ? 1 : 0;
                    printer.TienePapel = status.TienePapel ? 1 : 0;
                    printer.TapaAbierta = status.TapaAbierta ? 1 : 0;
                    printer.UltimoCheck = DateTime.Now.ToString("o");

                    _db.Update(printer);

                    // Detectar transiciones de estado para emitir notificaciones gRPC
                    if (!wasOnline && status.Online)
                    {
                        // Transición OFFLINE → ONLINE: re-encolar jobs WAITING y notificar
                        Log.InfoFormat("[MONITOR] ✓ {0} ({1}) ONLINE — re-encolando jobs en espera",
                            printer.Nombre ?? printer.ImpresoraId, printer.Ip);

                        RequeueWaitingJobs(printer.ImpresoraId); // Mover jobs WAITING → PENDING en la cola
                        // Notificar ONLINE via gRPC → solo servidores (evento de infraestructura)
                        NotifyPrinterChange(printer.ImpresoraId, printer.Nombre,
                            NotificationType.Online, "Impresora en línea");
                    }
                    else if (wasOnline && !status.Online)
                    {
                        // Transición ONLINE → OFFLINE: notificar a servidores
                        Log.WarnFormat("[MONITOR] ✗ {0} ({1}) OFFLINE — {2}",
                            printer.Nombre ?? printer.ImpresoraId, printer.Ip,
                            status.ErrorMessage ?? "sin conexión");
                        // Notificar OFFLINE via gRPC → solo servidores
                        NotifyPrinterChange(printer.ImpresoraId, printer.Nombre,
                            NotificationType.Offline, status.ErrorMessage ?? "sin conexión");
                    }

                    // Detectar si la impresora sigue sin papel (ya fue marcada en la BD)
                    if (!status.TienePapel && printer.TienePapel == 0)
                    {
                        // Notificar SIN_PAPEL via gRPC → solo servidores
                        NotifyPrinterChange(printer.ImpresoraId, printer.Nombre,
                            NotificationType.SinPapel, "Impresora sin papel");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    printer.EstadoOnline = 0;
                    printer.UltimoCheck = DateTime.Now.ToString("o");
                    try { _db.Update(printer); } catch { }

                    Log.WarnFormat("[MONITOR] Error verificando {0}: {1}",
                        printer.Nombre ?? printer.ImpresoraId, ex.Message);
                }
            }
        }

        private void RequeueWaitingJobs(string impresoraId)
        {
            try
            {
                var waitingJobs = _db.Query<PrintJobEntity>(
                    "SELECT * FROM print_jobs WHERE impresora_id = ? AND estado = 'WAITING' ORDER BY prioridad ASC, fecha_creacion ASC",
                    impresoraId);

                if (waitingJobs == null || waitingJobs.Count == 0) return;

                foreach (var entity in waitingJobs)
                {
                    var job = PrintJob.FromEntity(entity);
                    _jobManager.ReEnqueue(job);
                }

                Log.InfoFormat("[MONITOR] Re-encolados {0} jobs WAITING para {1}",
                    waitingJobs.Count, impresoraId);
            }
            catch (Exception ex)
            {
                Log.Error("[MONITOR] Error re-encolando jobs WAITING: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Helper para enviar notificaciones de cambio de estado de impresora via gRPC.
        /// Mismo patrón que NotifyIfAvailable en PrintWorker: captura errores
        /// silenciosamente para no afectar el ciclo de monitoreo.
        /// Solo envía a servidores suscritos (los clientes no reciben eventos de infraestructura).
        /// </summary>
        private void NotifyPrinterChange(string impresoraId, string nombre, string tipo, string mensaje)
        {
            try
            {
                // Delegar al NotificationManager que difunde a servidores suscritos via gRPC
                NotificationManager.Instance.NotifyPrinterStatusChange(
                    impresoraId, nombre, tipo, mensaje);
            }
            catch (InvalidOperationException)
            {
                // NotificationManager aún no inicializado (GetInstance no fue llamado) — ignorar
            }
            catch (Exception ex)
            {
                // Error al notificar — loguear pero NO relanzar, el monitoreo debe continuar
                Log.Warn("[MONITOR] Error al notificar cambio de estado: " + ex.Message);
            }
        }

        public PrinterStatus CheckPrinterNow(string ip, int port)
        {
            int timeoutMs = ConfigManager.Instance.GetInt("TcpConnectTimeoutMs", 3000);
            return PrinterStatusChecker.CheckSync(ip, port, timeoutMs);
        }
    }
}
