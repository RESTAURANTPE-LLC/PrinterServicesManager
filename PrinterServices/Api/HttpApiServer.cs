using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Data;
using PrinterServices.Queue;

namespace PrinterServices.Api
{
    public class HttpApiServer
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(HttpApiServer));

        private readonly HttpListener _listener;
        private readonly ApiRouter _router;
        private readonly CancellationTokenSource _cts;
        private readonly int _port;

        public HttpApiServer(int port, PrinterServiceDb db, PrintJobManager jobManager)
        {
            _port = port;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(string.Format("http://+:{0}/", port));
            _router = new ApiRouter(db, jobManager);
        }

        public void Start()
        {
            _listener.Start();
            Task.Factory.StartNew(() => ListenLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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

                var result = await _router.RouteAsync(request.HttpMethod, request.Url.AbsolutePath, request);
                
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
