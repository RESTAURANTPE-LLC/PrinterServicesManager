using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Drivers;
using PrinterServices.Queue;
using PrinterServices.Transport;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Notifications;

namespace PrinterServices.Workers
{
    public class PrintWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrintWorker));

        private readonly PrintJobManager _jobManager;
        private readonly PrinterServiceDb _db;
        private readonly CancellationTokenSource _cts;

        public PrintWorker(PrintJobManager jobManager, PrinterServiceDb db)
        {
            _jobManager = jobManager;
            _db = db;
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            Task.Factory.StartNew(() => WorkLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Log.Info("[WORKER] PrintWorker iniciado");
        }

        public void Stop()
        {
            _cts.Cancel();
            Log.Info("[WORKER] PrintWorker detenido");
        }

        private async Task WorkLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                PrintJob job = null;
                try
                {
                    job = await _jobManager.DequeueAsync(ct);
                    if (job == null) continue;

                    job.Estado = PrintJobStatus.Printing;
                    Log.InfoFormat("[WORKER] Procesando job {0} → {1} ({2})",
                        job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, job.ImpresoraIp);

                    await ProcessJobAsync(job, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("[WORKER] Error inesperado en work loop", ex);
                    if (job != null)
                    {
                        HandleFailure(job, ex.Message);
                    }
                }
            }
        }

        private async Task ProcessJobAsync(PrintJob job, CancellationToken ct)
        {
            var cfg = ConfigManager.Instance;
            int port = job.Puerto > 0 ? job.Puerto : cfg.GetInt("DefaultPrinterPort", 9100);
            int connectTimeoutMs = cfg.GetInt("TcpConnectTimeoutMs", 3000);

            // Pre-check: verificar si la impresora está online antes de intentar
            var printerStatus = await Monitoring.PrinterStatusChecker.CheckAsync(
                job.ImpresoraIp, port, connectTimeoutMs, ct);

            if (!printerStatus.Online)
            {
                Log.WarnFormat("[WORKER] Job {0} — impresora {1} ({2}) OFFLINE, moviendo a WAITING",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, job.ImpresoraIp);

                string offlineMsg = "Impresora offline: " + (printerStatus.ErrorMessage ?? "sin conexión");
                _jobManager.MarkWaiting(job, offlineMsg);
                LogPrint(job, "WAITING", "Impresora offline");
                // Notificar WAITING via gRPC → servidores + cliente origen
                NotifyIfAvailable(n => n.NotifyPrintWaiting(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen, offlineMsg));
                return;
            }

            if (!printerStatus.TienePapel)
            {
                Log.WarnFormat("[WORKER] Job {0} — impresora {1} SIN PAPEL, moviendo a WAITING",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId);

                _jobManager.MarkWaiting(job, "Sin papel");
                LogPrint(job, "WAITING", "Sin papel");
                // Notificar WAITING por sin papel via gRPC → servidores + cliente origen
                NotifyIfAvailable(n => n.NotifyPrintWaiting(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen, "Sin papel"));
                return;
            }

            IPrinterDriver driver = DriverFactory.GetDriver(job.PrinterModel);
            Log.DebugFormat("[WORKER] Driver seleccionado: {0} para modelo {1}", driver.ModelName, job.PrinterModel ?? "null");

            // Construir payload ESC/POS
            byte[] payload = BuildPayload(driver, job);

            // Enviar por cada copia
            for (int copia = 0; copia < job.Copias; copia++)
            {
                if (job.Copias > 1)
                {
                    Log.DebugFormat("[WORKER] Job {0} — copia {1}/{2}", job.JobId, copia + 1, job.Copias);
                }

                bool sent = await SendWithRetry(job, payload, ct);
                if (!sent)
                {
                    return; // Ya se manejó el failure
                }
            }

            // Éxito
            _jobManager.MarkDone(job);
            LogPrint(job, "DONE", "Impresión completada");
            // Notificar éxito via gRPC → servidores + cliente origen
            NotifyIfAvailable(n => n.NotifyPrintSuccess(
                job.JobId, job.ComandaId, job.ImpresoraId,
                job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen));
        }

        private byte[] BuildPayload(IPrinterDriver driver, PrintJob job)
        {
            // Si tiene lineasimprimir, usar modo estructurado
            if (!string.IsNullOrEmpty(job.LineasImprimirJson))
            {
                var lineas = LineaParser.ParseFromJson(job.LineasImprimirJson);
                if (lineas.Count > 0)
                {
                    Log.DebugFormat("[WORKER] Job {0} — modo LINEAS ({1} líneas)", job.JobId, lineas.Count);
                    return LineaParser.BuildFromLineas(driver, lineas);
                }
            }

            // Modo tradicional: cadena de texto plano
            Log.DebugFormat("[WORKER] Job {0} — modo CADENA", job.JobId);
            var builder = new EscPosCommandBuilder(driver);
            builder.Init();

            // Aplicar tamaño de letra si viene
            if (!string.IsNullOrEmpty(job.TamanioLetra))
            {
                builder.SetLetterSize(job.TamanioLetra);
            }

            // Texto
            if (!string.IsNullOrEmpty(job.Contenido))
            {
                builder.Text(job.Contenido);
            }

            // Abrir gaveta si se solicitó
            if (job.AbreGaveta)
            {
                builder.OpenCashDrawer();
            }

            // Corte
            builder.Cut(CutType.Partial);

            return builder.Build();
        }

        private async Task<bool> SendWithRetry(PrintJob job, byte[] payload, CancellationToken ct)
        {
            var cfg = ConfigManager.Instance;
            int port = job.Puerto > 0 ? job.Puerto : cfg.GetInt("DefaultPrinterPort", 9100);
            int maxRetries = cfg.GetInt("MaxRetries", 3);
            int retryBackoffBaseMs = cfg.GetInt("RetryBackoffBaseMs", 500);

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    int backoff = retryBackoffBaseMs * (1 << (attempt - 1)); // exponential: 500, 1000, 2000
                    Log.InfoFormat("[WORKER] Job {0} — retry {1}/{2}, esperando {3}ms",
                        job.JobId, attempt, maxRetries, backoff);
                    await Task.Delay(backoff, ct);
                }

                using (var transport = new TcpTransport(job.ImpresoraIp, port))
                {
                    try
                    {
                        await transport.ConnectAsync(ct);
                        await transport.SendAsync(payload, ct);
                        transport.Disconnect();

                        Log.DebugFormat("[WORKER] Job {0} — datos enviados ({1} bytes)", job.JobId, payload.Length);
                        return true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.WarnFormat("[WORKER] Job {0} — fallo intento {1}/{2}: {3}",
                            job.JobId, attempt + 1, maxRetries + 1, ex.Message);

                        if (attempt == maxRetries)
                        {
                            HandleFailure(job, ex.Message);
                            return false;
                        }
                    }
                }
            }

            return false;
        }

        private void HandleFailure(PrintJob job, string error)
        {
            if (job.Reintentos < job.MaxReintentos)
            {
                _jobManager.Retry(job);
                LogPrint(job, "RETRY", error);
                // Notificar REINTENTO via gRPC → solo servidores (informativo, el job aún no terminó)
                NotifyIfAvailable(n => n.NotifyPrintRetry(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen, job.Reintentos + 1));
            }
            else
            {
                _jobManager.MarkFailed(job, error);
                LogPrint(job, "FAILED", error);
                // Notificar FALLIDA via gRPC → servidores + cliente origen (todos los reintentos agotados)
                NotifyIfAvailable(n => n.NotifyPrintFailed(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen,
                    error, job.Reintentos));
            }
        }

        /// <summary>
        /// Helper para enviar notificaciones de forma segura.
        /// Si NotificationManager aún no fue inicializado (ej: durante arranque),
        /// captura InvalidOperationException y la ignora silenciosamente.
        /// Cualquier otro error se loguea pero NO detiene el flujo de impresión.
        /// Esto desacopla el worker de gRPC: si las notificaciones fallan, la impresión sigue.
        /// </summary>
        private void NotifyIfAvailable(Action<NotificationManager> action)
        {
            try
            {
                action(NotificationManager.Instance); // Ejecuta la acción de notificación
            }
            catch (InvalidOperationException)
            {
                // NotificationManager aún no inicializado (GetInstance no fue llamado) — ignorar
            }
            catch (Exception ex)
            {
                // Error al notificar — loguear pero NO relanzar, la impresión no debe fallar por esto
                Log.Warn("[WORKER] Error al notificar: " + ex.Message);
            }
        }

        private void LogPrint(PrintJob job, string estado, string mensaje)
        {
            try
            {
                var log = new PrintLogEntity
                {
                    JobId = job.JobId,
                    ImpresoraId = job.ImpresoraId,
                    ImpresoraNombre = job.ImpresoraNombre,
                    ImpresoraIp = job.ImpresoraIp,
                    Estado = estado,
                    Mensaje = mensaje,
                    Reintentos = job.Reintentos,
                    Fecha = DateTime.Now.ToString("o"),
                    DeviceIdOrigen = job.DeviceIdOrigen
                };
                _db.Insert(log);
            }
            catch (Exception ex)
            {
                Log.Error("[WORKER] Error al registrar log de impresión: " + ex.Message, ex);
            }
        }
    }
}
