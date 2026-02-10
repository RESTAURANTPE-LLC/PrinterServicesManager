using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Data.Models;
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

                    // Detectar cambio de estado
                    if (!wasOnline && status.Online)
                    {
                        Log.InfoFormat("[MONITOR] ✓ {0} ({1}) ONLINE — re-encolando jobs en espera",
                            printer.Nombre ?? printer.ImpresoraId, printer.Ip);

                        // Re-encolar jobs WAITING de esta impresora
                        RequeueWaitingJobs(printer.ImpresoraId);
                    }
                    else if (wasOnline && !status.Online)
                    {
                        Log.WarnFormat("[MONITOR] ✗ {0} ({1}) OFFLINE — {2}",
                            printer.Nombre ?? printer.ImpresoraId, printer.Ip,
                            status.ErrorMessage ?? "sin conexión");
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

        public PrinterStatus CheckPrinterNow(string ip, int port)
        {
            int timeoutMs = ConfigManager.Instance.GetInt("TcpConnectTimeoutMs", 3000);
            return PrinterStatusChecker.CheckSync(ip, port, timeoutMs);
        }
    }
}
