using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Workers
{
    /// <summary>
    /// WORKER: Mide velocidad de red cada 2 horas (configurable).
    /// PROPOSITO: Registrar velocidad de descarga y latencia al gateway en BD.
    /// PATRON: Identico a NetworkWatcher — Thread LongRunning + CancellationToken.
    /// Tambien permite medicion manual via MeasureNow().
    /// </summary>
    public class NetworkSpeedWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkSpeedWorker));
        private readonly PrinterServiceDb _db;
        private readonly ConfigManager _config;
        private CancellationTokenSource _cts;
        private Task _workerTask;

        // Señal para medicion manual desde dashboard
        private readonly SemaphoreSlim _manualSignal = new SemaphoreSlim(0, 1);

        // URL para medir velocidad de descarga
        private const string SpeedTestUrl = "https://instaladores.restaurant.pe/printer.zip";
        private const int DownloadTimeoutSeconds = 10;

        public NetworkSpeedWorker(PrinterServiceDb db, ConfigManager config)
        {
            _db = db;
            _config = config;
        }

        public void Start()
        {
            if (_workerTask != null)
            {
                Log.Warn("[SPEED-WORKER] Ya esta iniciado, ignorando");
                return;
            }

            _cts = new CancellationTokenSource();

            _workerTask = Task.Factory.StartNew(
                () => RunLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Log.Info("[SPEED-WORKER] Iniciado (medicion de velocidad de red)");
        }

        public void Stop()
        {
            if (_cts != null)
            {
                Log.Info("[SPEED-WORKER] Deteniendo...");
                _cts.Cancel();
            }

            if (_workerTask != null)
            {
                try { _workerTask.Wait(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { Log.Warn("[SPEED-WORKER] Error esperando finalizacion: " + ex.Message); }
            }

            Log.Info("[SPEED-WORKER] Detenido");
        }

        /// <summary>
        /// Forzar una medicion inmediata (llamado desde el dashboard).
        /// Thread-safe: despierta el RunLoop sin esperar el intervalo.
        /// </summary>
        public void MeasureNow()
        {
            try
            {
                if (_manualSignal.CurrentCount == 0)
                    _manualSignal.Release();
            }
            catch { }
        }

        private void RunLoop(CancellationToken ct)
        {
            // Esperar 30s al arrancar para no interferir con la inicializacion
            try { Task.Delay(TimeSpan.FromSeconds(30), ct).Wait(ct); }
            catch { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = PerformMeasurement();
                    if (result != null)
                    {
                        _db.Insert(result);
                        Log.InfoFormat("[SPEED-WORKER] Medicion completada: {0:F1} Kbps, latencia {1}ms",
                            result.DownloadSpeedKbps, result.LatencyMs);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[SPEED-WORKER] Error en medicion: " + ex.Message);
                }

                // Esperar intervalo configurable (default 120 min) o señal manual
                try
                {
                    int intervalMinutes = _config.GetInt("NetworkSpeedIntervalMinutes", 120);
                    // Espera el intervalo O la señal manual (lo que ocurra primero)
                    _manualSignal.Wait(TimeSpan.FromMinutes(intervalMinutes), ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// Realiza una medicion de velocidad: ping al gateway + descarga parcial.
        /// </summary>
        private NetworkSpeedLogEntity PerformMeasurement()
        {
            // Obtener gateway actual de la BD
            var networkCurrent = _db.Table<NetworkCurrentEntity>().FirstOrDefault(n => n.Id == 1);
            string gatewayIp = networkCurrent?.GatewayIp;
            string networkId = networkCurrent?.NetworkId;

            // 1. ICMP Ping al gateway (3 pings, promedio)
            int latencyMs = 0;
            if (!string.IsNullOrEmpty(gatewayIp))
            {
                latencyMs = MeasureLatency(gatewayIp);
            }

            // 2. Medir velocidad de descarga
            double downloadKbps = MeasureDownloadSpeed();

            return new NetworkSpeedLogEntity
            {
                DownloadSpeedKbps = downloadKbps,
                LatencyMs = latencyMs,
                GatewayIp = gatewayIp ?? "N/A",
                NetworkId = networkId ?? "N/A",
                MeasuredAt = DateTime.Now.ToString("o")
            };
        }

        /// <summary>
        /// Mide latencia ICMP al gateway (3 pings, promedio).
        /// </summary>
        private int MeasureLatency(string ip)
        {
            try
            {
                using (var ping = new Ping())
                {
                    long totalMs = 0;
                    int successCount = 0;

                    for (int i = 0; i < 3; i++)
                    {
                        var reply = ping.Send(ip, 2000); // timeout 2s
                        if (reply.Status == IPStatus.Success)
                        {
                            totalMs += reply.RoundtripTime;
                            successCount++;
                        }
                    }

                    return successCount > 0 ? (int)(totalMs / successCount) : -1;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[SPEED-WORKER] Error ping a " + ip + ": " + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Mide velocidad de descarga: descarga parcial (~5 segundos) de printer.zip.
        /// Calcula throughput real en Kbps.
        /// </summary>
        private double MeasureDownloadSpeed()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                long bytesDownloaded = 0;

                var request = (HttpWebRequest)WebRequest.Create(SpeedTestUrl);
                request.Timeout = DownloadTimeoutSeconds * 1000;
                request.ReadWriteTimeout = DownloadTimeoutSeconds * 1000;
                request.Method = "GET";

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    var buffer = new byte[8192];
                    int bytesRead;

                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        bytesDownloaded += bytesRead;

                        // Abortar despues de ~5 segundos para no desperdiciar ancho de banda
                        if (sw.ElapsedMilliseconds > 5000)
                            break;
                    }
                }

                sw.Stop();

                if (sw.ElapsedMilliseconds < 100 || bytesDownloaded < 1024)
                {
                    Log.Warn("[SPEED-WORKER] Descarga muy corta para medir velocidad");
                    return 0;
                }

                // Calcular Kbps: (bytes * 8) / (ms / 1000) / 1024
                double kbps = (bytesDownloaded * 8.0) / (sw.ElapsedMilliseconds / 1000.0) / 1024.0;
                return Math.Round(kbps, 1);
            }
            catch (Exception ex)
            {
                Log.Warn("[SPEED-WORKER] Error midiendo velocidad: " + ex.Message);
                return 0;
            }
        }
    }
}
