using System;
using System.Diagnostics;
using Newtonsoft.Json;
using PrinterServices.Data;

namespace PrinterServices.Api.Controllers
{
    public class HealthController
    {
        private readonly PrinterServiceDb _db;
        private readonly DateTime _startTime;

        public HealthController(PrinterServiceDb db)
        {
            _db = db;
            _startTime = DateTime.Now;
        }

        public ApiResult GetHealth()
        {
            var uptime = DateTime.Now - _startTime;

            var response = new
            {
                status = "OK",
                version = Core.ServiceVersion.FullVersion,
                uptime = string.Format("{0}d {1}h {2}m {3}s", uptime.Days, uptime.Hours, uptime.Minutes, uptime.Seconds),
                uptimeSeconds = (int)uptime.TotalSeconds,
                database = _db != null ? "Connected" : "Disconnected",
                databasePath = _db != null ? _db.DatabasePath : null,
                timestamp = DateTime.Now.ToString("o")
            };

            return ApiResult.Ok(JsonConvert.SerializeObject(response));
        }
    }
}
