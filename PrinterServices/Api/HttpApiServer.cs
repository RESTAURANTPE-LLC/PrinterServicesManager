using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Queue;
using PrinterServices.Services.Printers;

namespace PrinterServices.Api
{
    public class HttpApiServer
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(HttpApiServer));

        private readonly HttpListener _listener;
        private readonly ApiRouter _router;
        private readonly CancellationTokenSource _cts;
        private readonly int _port;

        // Constructor original (compatibilidad).
        public HttpApiServer(int port, PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager)
            : this(port, db, jobManager, configManager, null, null, null) { }

        // Constructor con ProbeScheduler (compatibilidad intermedia).
        public HttpApiServer(int port, PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager,
            ProbeScheduler probeScheduler)
            : this(port, db, jobManager, configManager, probeScheduler, null, null) { }

        // Constructor con ProbeScheduler + DbMaintenanceWorker.
        public HttpApiServer(int port, PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager,
            ProbeScheduler probeScheduler, PrinterServices.Workers.DbMaintenanceWorker dbMaintenanceWorker)
            : this(port, db, jobManager, configManager, probeScheduler, dbMaintenanceWorker, null) { }

        // Constructor completo: recibe también el PrinterDiscoveryService para el
        // buscador de impresoras multi-protocolo del dashboard.
        public HttpApiServer(int port, PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager,
            ProbeScheduler probeScheduler,
            PrinterServices.Workers.DbMaintenanceWorker dbMaintenanceWorker,
            PrinterServices.Services.Discovery.PrinterDiscoveryService discoveryService)
        {
            _port = port;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _router = new ApiRouter(db, jobManager, configManager, probeScheduler, dbMaintenanceWorker, discoveryService);
        }

        public void Start()
        {
            // Forzar solo IPv4: HttpListener con http://+:port/ bindea IPv4+IPv6.
            // En PCs con múltiples NICs donde IPv6 no está configurado, HTTP.sys falla:
            // "No se puede enlazar con el transporte subyacente para [::]:port".
            // FIX: Bindear en cada IP local IPv4 individualmente, excluyendo IPv6.
            var ipv4Addresses = GetLocalIPv4Addresses();

            if (ipv4Addresses.Count > 0)
            {
                foreach (var ip in ipv4Addresses)
                {
                    string prefix = string.Format("http://{0}:{1}/", ip, _port);
                    _listener.Prefixes.Add(prefix);
                    Log.InfoFormat("[HTTP] Registrando prefijo IPv4: {0}", prefix);
                }
                // Siempre agregar localhost y 127.0.0.1 para conexiones locales.
                // RAZÓN: HttpListener distingue hostname — localhost ≠ 127.0.0.1.
                // Sin ambos, acceder por http://127.0.0.1:port/ da HTTP 400.
                _listener.Prefixes.Add(string.Format("http://localhost:{0}/", _port));
                _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", _port));
            }
            else
            {
                // Fallback si no se detectan IPs (raro)
                Log.Warn("[HTTP] No se detectaron IPs IPv4 locales, usando localhost");
                _listener.Prefixes.Add(string.Format("http://localhost:{0}/", _port));
            }

            // Retry para tolerar reinicios del servicio: el puerto puede seguir
            // ocupado brevemente por la instancia anterior que aún no liberó el socket.
            const int maxRetries = 3;
            const int retryDelayMs = 2000;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    _listener.Start();
                    break; // Éxito
                }
                catch (HttpListenerException ex)
                {
                    if (i < maxRetries - 1)
                    {
                        Log.WarnFormat("[HTTP] Puerto {0} ocupado (intento {1}/{2}): {3}. Reintentando en {4}ms...",
                            _port, i + 1, maxRetries, ex.Message, retryDelayMs);
                        System.Threading.Thread.Sleep(retryDelayMs);
                    }
                    else
                    {
                        Log.ErrorFormat("[HTTP] No se pudo iniciar en puerto {0} después de {1} intentos", _port, maxRetries);
                        throw; // Propagar en el último intento
                    }
                }
            }

            Log.InfoFormat("[HTTP] Servidor HTTP iniciado en puerto {0} (solo IPv4, {1} interfaces)",
                _port, ipv4Addresses.Count);

            Task.Factory.StartNew(() => ListenLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// Obtiene todas las direcciones IPv4 locales de la máquina (excluyendo loopback e IPv6).
        /// </summary>
        private static System.Collections.Generic.List<string> GetLocalIPv4Addresses()
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;

                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork // Solo IPv4
                            && !IPAddress.IsLoopback(addr.Address))                                     // Excluir 127.0.0.1
                        {
                            result.Add(addr.Address.ToString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger(typeof(HttpApiServer)).Warn("[HTTP] Error obteniendo IPs IPv4: " + ex.Message);
            }
            return result;
        }

        public void Stop()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    // Procesar cada request en un thread del pool (no bloquear el listener)
                    var _ = Task.Run(() => ProcessRequest(context));
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    // Shutdown normal
                    break;
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("[HTTP] Error en listener loop", ex);
                }
            }
        }

        private async Task ProcessRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                Log.DebugFormat("[HTTP] {0} {1}", request.HttpMethod, request.Url.AbsolutePath);

                // RAZÓN: Intentar ruteo normal primero
                var result = await _router.RouteAsync(request.HttpMethod, request.Url.AbsolutePath, request);

                // RAZÓN: Si result es null, es una ruta especial (dashboard HTML)
                if (result == null)
                {
                    // Delegar a HandleSpecialRoute que maneja HTML directamente
                    _router.HandleSpecialRoute(request.HttpMethod, request.Url.AbsolutePath, context);
                    return; // Ya se cerró el response en HandleSpecialRoute
                }

                response.StatusCode = result.StatusCode;
                response.ContentType = "application/json; charset=utf-8";

                // CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                byte[] buffer = Encoding.UTF8.GetBytes(result.Body ?? "{}");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("[HTTP] Error procesando {0} {1}: {2}", request.HttpMethod, request.Url.AbsolutePath, ex.Message);
                try
                {
                    response.StatusCode = 500;
                    byte[] errorBytes = Encoding.UTF8.GetBytes("{\"error\":\"Internal Server Error\"}");
                    response.ContentLength64 = errorBytes.Length;
                    response.ContentType = "application/json; charset=utf-8";
                    await response.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length);
                }
                catch { }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }
    }
}
