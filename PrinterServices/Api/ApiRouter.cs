using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Api.Controllers;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Queue;

namespace PrinterServices.Api
{
    public class ApiResult
    {
        public int StatusCode { get; set; }
        public string Body { get; set; }

        public ApiResult(int statusCode, string body)
        {
            StatusCode = statusCode;
            Body = body;
        }

        public static ApiResult Ok(string json)
        {
            return new ApiResult(200, json);
        }

        public static ApiResult NotFound()
        {
            return new ApiResult(404, "{\"error\":\"Not Found\"}");
        }

        public static ApiResult BadRequest(string message)
        {
            return new ApiResult(400, string.Format("{{\"error\":\"{0}\"}}", message));
        }

        public static ApiResult Error(string message)
        {
            return new ApiResult(500, string.Format("{{\"error\":\"{0}\"}}", message));
        }
    }

    public class ApiRouter
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ApiRouter));

        private readonly HealthController _healthController;
        private readonly PrintController _printController;
        private readonly JobController _jobController;
        private readonly PrinterController _printerController;
        private readonly ConfigController _configController;
        private readonly DashboardController _dashboardController;
        private readonly NetworkController _networkController;

        public ApiRouter(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager)
        {
            _healthController = new HealthController(db);
            _printController = new PrintController(jobManager);
            _jobController = new JobController(jobManager);
            _printerController = new PrinterController(db);
            _configController = new ConfigController(configManager);
            _dashboardController = new DashboardController(db, jobManager, configManager);
            _networkController = new NetworkController();
        }

        public async Task<ApiResult> RouteAsync(string method, string path, HttpListenerRequest request)
        {
            // Normalizar path
            path = path.TrimEnd('/').ToLowerInvariant();

            // ── Health ──
            if (method == "GET" && path == "/api/health")
            {
                return _healthController.GetHealth();
            }

            // RAZÓN: Capturar IP real del cliente HTTP para trazabilidad y comunicación de retorno
            string clientIp = request.RemoteEndPoint != null ? request.RemoteEndPoint.Address.ToString() : null;

            // ── Print ──
            if (method == "POST" && path == "/api/print/comanda")
            {
                string body = await ReadBodyAsync(request);
                return _printController.PostComanda(body, clientIp);
            }
            if (method == "POST" && path == "/api/print/comandas")
            {
                string body = await ReadBodyAsync(request);
                return _printController.PostComandas(body, clientIp);
            }
            if (method == "POST" && path == "/api/print/venta")
            {
                string body = await ReadBodyAsync(request);
                return _printController.PostVenta(body, clientIp);
            }
            if (method == "POST" && path == "/api/print/precuenta")
            {
                string body = await ReadBodyAsync(request);
                return _printController.PostPrecuenta(body, clientIp);
            }

            // ── Printers ──
            if (method == "GET" && path == "/api/printer/status")
            {
                return _printerController.GetAllPrinters();
            }
            if (method == "GET" && path.StartsWith("/api/printer/status/"))
            {
                string id = path.Substring("/api/printer/status/".Length);
                return _printerController.GetPrinterStatus(id);
            }
            if (method == "POST" && path == "/api/printer/register")
            {
                string body = await ReadBodyAsync(request);
                return _printerController.RegisterPrinter(body);
            }
            if (method == "POST" && path == "/api/printers/sync")
            {
                string body = await ReadBodyAsync(request); // Leer body JSON del request
                return _printerController.SyncPrinters(body); // Llamar a método de sincronización
            }

            // ── Jobs ──
            if (method == "POST" && path == "/api/jobs/status")
            {
                // Fase 23: Consulta bulk de estados de jobs (usado por QuipuNetX para failover)
                string body = await ReadBodyAsync(request); // Leer body JSON con lista de jobIds
                return _jobController.GetJobsStatus(body);  // Retornar estados actuales
            }
            if (method == "GET" && path == "/api/jobs/pending")
            {
                return _jobController.GetPendingJobs();
            }
            if (method == "DELETE" && path.StartsWith("/api/job/"))
            {
                // DELETE /api/job/{jobId} — Eliminar job y sus logs
                string jobId = path.Substring("/api/job/".Length);
                return _jobController.DeleteJob(jobId);
            }
            if (method == "GET" && path.StartsWith("/api/job/"))
            {
                string jobId = path.Substring("/api/job/".Length);
                return _jobController.GetJob(jobId);
            }
            if (method == "POST" && path.StartsWith("/api/job/") && path.EndsWith("/retry"))
            {
                // /api/job/{jobId}/retry
                string segment = path.Substring("/api/job/".Length);
                string jobId = segment.Substring(0, segment.Length - "/retry".Length);
                return _jobController.RetryJob(jobId);
            }

            // ── Config ──
            if (method == "GET" && path == "/api/config")
            {
                return _configController.GetAll();
            }
            if (method == "GET" && path.StartsWith("/api/config/category/"))
            {
                string category = path.Substring("/api/config/category/".Length);
                return _configController.GetByCategory(category);
            }
            if (method == "GET" && path.StartsWith("/api/config/"))
            {
                string key = path.Substring("/api/config/".Length);
                return _configController.GetSetting(key);
            }
            if (method == "PUT" && path == "/api/config")
            {
                string body = await ReadBodyAsync(request);
                return _configController.UpdateSetting(body);
            }
            if (method == "PUT" && path == "/api/config/batch")
            {
                string body = await ReadBodyAsync(request);
                return _configController.UpdateBatch(body);
            }
            if (method == "POST" && path == "/api/config/sync")
            {
                // Fase 23: Sincronizar configuraciones desde QuipuNetX (ej: ExpirarImpresionDespuesDe)
                string body = await ReadBodyAsync(request); // Leer body JSON con settings
                return _configController.SyncConfig(body);  // Actualizar settings en ConfigManager
            }
            if (method == "POST" && path.StartsWith("/api/config/") && path.EndsWith("/reset"))
            {
                string segment = path.Substring("/api/config/".Length);
                string key = segment.Substring(0, segment.Length - "/reset".Length);
                return _configController.ResetSetting(key);
            }
            if (method == "POST" && path == "/api/config/reset-all")
            {
                return _configController.ResetAll();
            }

            // ── Dashboard (Fase 8) ──
            // RAZÓN: Rutas especiales retornan null → HttpApiServer delega a HandleSpecialRoute
            // porque necesitan acceso directo a HttpListenerContext (HTML, paginación, etc.)
            if (method == "GET" && path == "/api/dashboard")
            {
                return null; // Ruta especial: retorna HTML directamente
            }
            if (method == "GET" && path == "/api/dashboard/data")
            {
                return null; // Ruta especial: JSON con toda la info del sistema
            }
            if (method == "GET" && path.StartsWith("/api/dashboard/history"))
            {
                return null; // Ruta especial: historial paginado (query params)
            }
            if (method == "GET" && path.StartsWith("/api/dashboard/notifications"))
            {
                return null; // Ruta especial: historial de notificaciones/callbacks paginado
            }
            if (method == "GET" && path.StartsWith("/api/dashboard/job/"))
            {
                return null; // Ruta especial: detalle de job por ID
            }
            if (method == "GET" && path.StartsWith("/api/dashboard/network-history"))
            {
                return null; // Ruta especial: historial de alertas de red paginado
            }
            if (method == "GET" && path.StartsWith("/api/dashboard/connectivity"))
            {
                return null; // Ruta especial: reporte de conectividad de impresoras
            }

            // ── Printer IP Reset (cross-subnet) ──
            if (method == "GET" && path == "/api/printer/resetip")
            {
                // Extraer query params: ipactual, ipfinal, mask, gateway, community
                var qs = request.QueryString; // QueryString de la petición HTTP
                string ipActual = qs["ipactual"];       // IP actual de la impresora
                string ipFinal = qs["ipfinal"];         // IP nueva deseada
                string mask = qs["mask"];               // Máscara (opcional)
                string gateway = qs["gateway"];         // Gateway (opcional)
                string community = qs["community"];     // SNMP community (opcional)
                return _networkController.ResetIp(ipActual, ipFinal, mask, gateway, community);
            }

            // TODO Fase 5: GET  /api/notifications/{deviceId}
            // TODO Fase 8: GET  /api/network/status

            return ApiResult.NotFound();
        }

        /// <summary>
        /// Maneja rutas especiales del dashboard que necesitan acceso directo a HttpListenerContext.
        /// RAZÓN: Dashboard retorna HTML, no JSON - necesita control completo del response.
        /// </summary>
        public void HandleSpecialRoute(string method, string path, HttpListenerContext ctx)
        {
            // RAZÓN: Preservar path original para extraer parámetros con case correcto (jobIds son GUIDs)
            string originalPath = path.TrimEnd('/');
            path = originalPath.ToLowerInvariant();

            if (method == "GET" && path == "/api/dashboard")
            {
                _dashboardController.HandleDashboard(ctx);
                return;
            }

            if (method == "GET" && path == "/api/dashboard/data")
            {
                _dashboardController.HandleDashboardData(ctx);
                return;
            }

            // ── Dashboard: Detalle de Job ──
            if (method == "GET" && path.StartsWith("/api/dashboard/job/"))
            {
                // RAZÓN: Usar originalPath para preservar el case del jobId (GUID)
                string jobId = originalPath.Substring("/api/dashboard/job/".Length);
                _dashboardController.HandleJobDetail(ctx, jobId);
                return;
            }

            // ── Dashboard: Historial paginado ──
            if (method == "GET" && path.StartsWith("/api/dashboard/history"))
            {
                // Parsear query params: ?page=1&limit=10
                var query = ctx.Request.QueryString;
                int page = 1;
                int limit = 10;
                
                if (!string.IsNullOrEmpty(query["page"]))
                {
                    int.TryParse(query["page"], out page);
                }
                if (!string.IsNullOrEmpty(query["limit"]))
                {
                    int.TryParse(query["limit"], out limit);
                }
                
                _dashboardController.HandleHistory(ctx, page, limit);
                return;
            }

            // ── Dashboard: Historial de Notificaciones (callbacks a clientes/servidor) ──
            if (method == "GET" && path.StartsWith("/api/dashboard/notifications"))
            {
                var query = ctx.Request.QueryString;
                int page = 1;
                int limit = 50;                                            // Default 50 items por página
                
                if (!string.IsNullOrEmpty(query["page"]))
                {
                    int.TryParse(query["page"], out page);
                }
                if (!string.IsNullOrEmpty(query["limit"]))
                {
                    int.TryParse(query["limit"], out limit);
                }
                
                _dashboardController.HandleNotificationHistory(ctx, page, limit);
                return;
            }

            // ── Dashboard: Historial de Red ──
            if (method == "GET" && path.StartsWith("/api/dashboard/network-history"))
            {
                var query = ctx.Request.QueryString;
                int page = 1;
                int limit = 20;
                
                if (!string.IsNullOrEmpty(query["page"]))
                {
                    int.TryParse(query["page"], out page);
                }
                if (!string.IsNullOrEmpty(query["limit"]))
                {
                    int.TryParse(query["limit"], out limit);
                }
                
                _dashboardController.HandleNetworkHistory(ctx, page, limit);
                return;
            }

            // ── Dashboard: Reporte de Conectividad ──
            if (method == "GET" && path.StartsWith("/api/dashboard/connectivity"))
            {
                var query = ctx.Request.QueryString;
                int page = 1;
                int limit = 50;
                string impresoraId = null;
                
                if (!string.IsNullOrEmpty(query["page"]))
                {
                    int.TryParse(query["page"], out page);
                }
                if (!string.IsNullOrEmpty(query["limit"]))
                {
                    int.TryParse(query["limit"], out limit);
                }
                if (!string.IsNullOrEmpty(query["impresora_id"]))
                {
                    impresoraId = query["impresora_id"];
                }
                
                _dashboardController.HandleConnectivityReport(ctx, page, limit, impresoraId);
                return;
            }

            // Si llegamos aquí, no es ruta especial
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }

        public static async Task<string> ReadBodyAsync(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                return await reader.ReadToEndAsync();
            }
        }
    }
}
