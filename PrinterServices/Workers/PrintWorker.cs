using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Drivers;
using PrinterServices.Queue;
using PrinterServices.Transport;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Workers
{
    public class PrintWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrintWorker));

        private readonly PrintJobManager _jobManager;
        private readonly PrinterServiceDb _db;
        private readonly CancellationTokenSource _cts;

        private readonly int _maxRetries;
        private readonly int _retryBackoffBaseMs;

        public PrintWorker(PrintJobManager jobManager, PrinterServiceDb db)
        {
            _jobManager = jobManager;
            _db = db;
            _cts = new CancellationTokenSource();

            int maxRetries;
            string maxRetriesStr = ConfigurationManager.AppSettings["MaxRetries"];
            _maxRetries = (!string.IsNullOrEmpty(maxRetriesStr) && int.TryParse(maxRetriesStr, out maxRetries))
                ? maxRetries : 3;

            int retryBase;
            string retryBaseStr = ConfigurationManager.AppSettings["RetryBackoffBaseMs"];
            _retryBackoffBaseMs = (!string.IsNullOrEmpty(retryBaseStr) && int.TryParse(retryBaseStr, out retryBase))
                ? retryBase : 500;
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
        }

        private byte[] BuildPayload(IPrinterDriver driver, PrintJob job)
        {
            var parts = new List<byte[]>();

            // Init
            parts.Add(driver.GetInitSequence());

            // Texto
            if (!string.IsNullOrEmpty(job.Contenido))
            {
                parts.Add(driver.GetTextBytes(job.Contenido));
            }

            // Corte
            parts.Add(driver.GetCutCommand(CutType.Partial));

            // Calcular tamaño total
            int totalLength = 0;
            foreach (var part in parts)
            {
                totalLength += part.Length;
            }

            // Combinar
            var result = new byte[totalLength];
            int offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        private async Task<bool> SendWithRetry(PrintJob job, byte[] payload, CancellationToken ct)
        {
            int port = job.Puerto > 0 ? job.Puerto : 9100;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    int backoff = _retryBackoffBaseMs * (1 << (attempt - 1)); // exponential: 500, 1000, 2000
                    Log.InfoFormat("[WORKER] Job {0} — retry {1}/{2}, esperando {3}ms",
                        job.JobId, attempt, _maxRetries, backoff);
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
                            job.JobId, attempt + 1, _maxRetries + 1, ex.Message);

                        if (attempt == _maxRetries)
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
            }
            else
            {
                _jobManager.MarkFailed(job, error);
                LogPrint(job, "FAILED", error);
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
