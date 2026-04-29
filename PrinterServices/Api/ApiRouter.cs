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
using PrinterServices.Services.Printers;


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

        public static NetworkSpeedWorker SpeedWorker { get; set; }
        public static NetworkDiscoveryWorker DiscoveryWorker { get; set; }

        private readonly HealthController _healthController;
        private readonly PrintController _printController;
        private readonly JobController _jobController;
        private readonly PrinterController _printerController;
        private readonly ConfigController _configController;
        private readonly DashboardController _dashboardController;
        private readonly NetworkController _networkController;

        // Constructor original (compatibilidad).
        public ApiRouter(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager)
            : this(db, jobManager, configManager, null, null, null) { }

        // Constructor con ProbeScheduler (compatibilidad intermedia).
        public ApiRouter(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager, ProbeScheduler probeScheduler)
            : this(db, jobManager, configManager, probeScheduler, null, null) { }

        // Constructor con ProbeScheduler + DbMaintenanceWorker.
        public ApiRouter(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager,
            ProbeScheduler probeScheduler, PrinterServices.Workers.DbMaintenanceWorker dbMaintenanceWorker)
            : this(db, jobManager, configManager, probeScheduler, dbMaintenanceWorker, null) { }

        // Constructor completo: recibe también el PrinterDiscoveryService para el
        // buscador de impresoras multi-protocolo del dashboard.
        public ApiRouter(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager configManager,
            ProbeScheduler probeScheduler,
            PrinterServices.Workers.DbMaintenanceWorker dbMaintenanceWorker,
            PrinterServices.Services.Discovery.PrinterDiscoveryService discoveryService)
        {
            _healthController = new HealthController(db);
            _printController = new PrintController(jobManager);
            _jobController = new JobController(jobManager);
            _printerController = new PrinterController(db, probeScheduler);
            _configController = new ConfigController(configManager);
            _dashboardController = new DashboardController(db, jobManager, configManager, DateTime.Now, dbMaintenanceWorker, discoveryService);
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
            if (method == "PUT" && path == "/api/printer/update")
            {
                string body = await ReadBodyAsync(request);
                return _printerController.UpdatePrinter(body);
            }
            if (method == "POST" && path == "/api/printers/sync")
            {
                string body = await ReadBodyAsync(request); // Leer body JSON del request
                return _printerController.SyncPrinters(body); // Llamar a método de sincronización
            }
            // POST /api/printers/{id}/probe-capabilities — fuerza probe manual de capacidades ESC/POS.
            // Devuelve 503 si el probe no fue habilitado en este servicio.
            if (method == "POST" && path.StartsWith("/api/printers/") && path.EndsWith("/probe-capabilities"))
            {
                string segmento = path.Substring("/api/printers/".Length);
                string id = segmento.Substring(0, segmento.Length - "/probe-capabilities".Length);
                return _printerController.ProbeCapabilities(id);
            }
            // GET /api/printers/{id}/probe-log — devuelve el transcript del último probe
            // de capacidades (comandos enviados + respuestas + decisiones), ordenado
            // cronológicamente. Usado por el modal del dashboard para mostrar qué pasó
            // durante el análisis de la impresora.
            if (method == "GET" && path.StartsWith("/api/printers/") && path.EndsWith("/probe-log"))
            {
                string segmento = path.Substring("/api/printers/".Length);
                string id = segmento.Substring(0, segmento.Length - "/probe-log".Length);
                return _printerController.ProbeLog(id);
            }
            // ── USB Discovery: Enumerar impresoras USB conectadas ──
            if (method == "GET" && path == "/api/printer/usb/discover")
            {
                return _printerController.DiscoverUsbPrinters();
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
            // Retry DEBE evaluarse ANTES de las rutas genéricas GET/DELETE /api/job/{id}
            // RAZÓN: GET /api/job/{id} captura /api/job/{id}/retry como jobId="{id}/retry" → NotFound
            if ((method == "POST" || method == "GET") && path.StartsWith("/api/job/") && path.EndsWith("/retry"))
            {
                // /api/job/{jobId}/retry — acepta GET (dashboard/browser) y POST
                string segment = path.Substring("/api/job/".Length);
                string jobId = segment.Substring(0, segment.Length - "/retry".Length);
                return _jobController.RetryJob(jobId);
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

            // ── Config ──
            if (method == "GET" && path == "/api/config")
            {
                return _configController.GetAll();
            }
            // GET /api/config/set?key=X&value=Y&minvalue=Z&maxvalue=W
            // IMPORTANTE: Debe ir ANTES de /api/config/{key} para que no lo intercepte
            if (method == "GET" && path == "/api/config/set")
            {
                string key = request.QueryString["key"];
                string value = request.QueryString["value"];
                string minValue = request.QueryString["minvalue"];
                string maxValue = request.QueryString["maxvalue"];

                if (string.IsNullOrEmpty(key) || value == null)
                {
                    return ApiResult.BadRequest("Parámetros requeridos: ?key=X&value=Y  (opcionales: &minvalue=Z&maxvalue=W)");
                }

                if (minValue != null || maxValue != null)
                {
                    _configController.UpdateSettingRange(key, minValue, maxValue);
                }

                string json = "{\"key\":\"" + key + "\",\"value\":\"" + value + "\"}";
                return _configController.UpdateSetting(json);
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

            // ── Reset impresora remoto ──
            if (method == "POST" && path.StartsWith("/api/printer/reset/"))
            {
                string impresoraId = path.Substring("/api/printer/reset/".Length);
                return _printerController.ResetPrinter(impresoraId);
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
            if (method == "POST" && path == "/api/dashboard/sql") { return null; }
            if (method == "GET" && path.StartsWith("/api/dashboard/speed-history")) { return null; }
            if (method == "POST" && path == "/api/dashboard/speed-measure") { return null; }
            if (method == "GET" && path.StartsWith("/api/dashboard/network-devices")) { return null; }
            if (method == "POST" && path == "/api/dashboard/network-scan") { return null; }
            if (method == "POST" && path == "/api/dashboard/update") { return null; }
            if (method == "GET" && path == "/api/dashboard/update-status") { return null; }
            if (method == "GET" && path == "/api/dashboard/quipunet-health") { return null; }
            if (method == "GET" && path == "/api/dashboard/quipunet-screenshot") { return null; }
            // Mantenimiento de BD: info (tamaño, top tablas, última purga) y disparar purga manual (solo localhost)
            if (method == "GET" && path == "/api/dashboard/db-maintenance/info") { return null; }
            if (method == "POST" && path == "/api/dashboard/db-maintenance/run") { return null; }
            // Descubrimiento multi-protocolo de impresoras
            if (method == "POST" && path == "/api/dashboard/discovery/start") { return null; }
            if (method == "GET" && path.StartsWith("/api/dashboard/discovery/status/")) { return null; }
            if (method == "POST" && path == "/api/dashboard/discovery/add") { return null; }

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

            // ── Dashboard: Bitmap guardado de un job (JPG) ──
            // IMPORTANTE: va ANTES del handler genérico de /api/dashboard/job/ para
            // que no se lo trague como un jobId con suffix raro.
            if (method == "GET" && path.StartsWith("/api/dashboard/job/") && path.EndsWith("/bitmap-info"))
            {
                string seg = originalPath.Substring("/api/dashboard/job/".Length);
                string jobId = seg.Substring(0, seg.Length - "/bitmap-info".Length);
                _dashboardController.HandleJobBitmapInfo(ctx, jobId);
                return;
            }
            if (method == "GET" && path.StartsWith("/api/dashboard/job/") && path.EndsWith("/bitmap"))
            {
                string seg = originalPath.Substring("/api/dashboard/job/".Length);
                string jobId = seg.Substring(0, seg.Length - "/bitmap".Length);
                _dashboardController.HandleJobBitmapJpeg(ctx, jobId);
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

            // ── Health Dashboard routes ──
            if (method == "POST" && path == "/api/dashboard/sql") { _dashboardController.HandleSqlQuery(ctx); return; }
            if (method == "GET" && path.StartsWith("/api/dashboard/speed-history")) { var q=ctx.Request.QueryString; int lim=48; if(!string.IsNullOrEmpty(q["limit"])) int.TryParse(q["limit"],out lim); _dashboardController.HandleSpeedHistory(ctx,lim); return; }
            if (method == "POST" && path == "/api/dashboard/speed-measure") { if(SpeedWorker!=null){SpeedWorker.MeasureNow();_dashboardController.WriteJsonResponse(ctx,200,new{status="OK"});}else{_dashboardController.WriteJsonResponse(ctx,503,new{error="SpeedWorker no disponible"});} return; }
            if (method == "GET" && path.StartsWith("/api/dashboard/network-devices")) { _dashboardController.HandleNetworkDevices(ctx); return; }
            if (method == "POST" && path == "/api/dashboard/network-scan") { if(DiscoveryWorker!=null){DiscoveryWorker.ScanNow();_dashboardController.HandleNetworkDevices(ctx);}else{_dashboardController.WriteJsonResponse(ctx,503,new{error="DiscoveryWorker no disponible"});} return; }
            if (method == "POST" && path == "/api/dashboard/update") { _dashboardController.HandleUpdateRequest(ctx); return; }
            if (method == "GET" && path == "/api/dashboard/update-status") { _dashboardController.HandleUpdateStatus(ctx); return; }
            if (method == "GET" && path == "/api/dashboard/quipunet-health") { _dashboardController.HandleQuipuNetHealth(ctx); return; }
            if (method == "GET" && path == "/api/dashboard/quipunet-screenshot") { _dashboardController.HandleQuipuNetScreenshot(ctx); return; }
            // Mantenimiento de BD: info (tamaño, top tablas, última purga) y purga manual (solo localhost)
            if (method == "GET" && path == "/api/dashboard/db-maintenance/info") { _dashboardController.HandleDbMaintenanceInfo(ctx); return; }
            if (method == "POST" && path == "/api/dashboard/db-maintenance/run") { _dashboardController.HandleDbMaintenanceRun(ctx); return; }
            // Discovery multi-protocolo
            if (method == "POST" && path == "/api/dashboard/discovery/start") { _dashboardController.HandleDiscoveryStart(ctx); return; }
            if (method == "GET" && path.StartsWith("/api/dashboard/discovery/status/"))
            {
                string id = originalPath.Substring("/api/dashboard/discovery/status/".Length);
                _dashboardController.HandleDiscoveryStatus(ctx, id);
                return;
            }
            if (method == "POST" && path == "/api/dashboard/discovery/add") { _dashboardController.HandleDiscoveryAdd(ctx); return; }

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
