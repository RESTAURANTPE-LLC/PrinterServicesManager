using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Api.Controllers;
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

        public ApiRouter(PrinterServiceDb db, PrintJobManager jobManager)
        {
            _healthController = new HealthController(db);
            _printController = new PrintController(jobManager);
            _jobController = new JobController(jobManager);
            _printerController = new PrinterController(db);
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

            // ── Print ──
            if (method == "POST" && path == "/api/print/comanda")
            {
                string body = await ReadBodyAsync(request);
                return _printController.PostComanda(body);
            }
            if (method == "POST" && path == "/api/print/comandas")
            {
                string body = await ReadBodyAsync(request);
                return _printController.PostComandas(body);
            }
            if (method == "POST" && path == "/api/print/venta")
            {
                string body = await ReadBodyAsync(request);
                return _printController.PostVenta(body);
            }
            if (method == "POST" && path == "/api/print/precuenta")
            {
                string body = await ReadBodyAsync(request);
                return _printController.PostPrecuenta(body);
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

            // ── Jobs ──
            if (method == "GET" && path == "/api/jobs/pending")
            {
                return _jobController.GetPendingJobs();
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

            // TODO Fase 5: GET  /api/notifications/{deviceId}
            // TODO Fase 8: GET  /api/network/status

            return ApiResult.NotFound();
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
