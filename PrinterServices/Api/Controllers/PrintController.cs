using System;
using System.Collections.Generic;
using System.Net;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PrinterServices.Queue;

namespace PrinterServices.Api.Controllers
{
    public class PrintController
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrintController));

        private readonly PrintJobManager _jobManager;

        public PrintController(PrintJobManager jobManager)
        {
            _jobManager = jobManager;
        }

        public ApiResult PostComanda(string body)
        {
            try
            {
                if (string.IsNullOrEmpty(body))
                {
                    return ApiResult.BadRequest("Body vacío");
                }

                var json = JObject.Parse(body);
                var job = ParsePrintJob(json);

                if (string.IsNullOrEmpty(job.ImpresoraIp))
                {
                    return ApiResult.BadRequest("impresora_ip es requerido");
                }

                string jobId = _jobManager.Enqueue(job);

                var response = new
                {
                    status = "ENQUEUED",
                    jobId = jobId,
                    impresora = job.ImpresoraNombre ?? job.ImpresoraId,
                    ip = job.ImpresoraIp
                };

                return ApiResult.Ok(JsonConvert.SerializeObject(response));
            }
            catch (JsonException ex)
            {
                Log.Error("[PRINT] Error parseando JSON: " + ex.Message);
                return ApiResult.BadRequest("JSON inválido: " + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("[PRINT] Error procesando comanda", ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult PostComandas(string body)
        {
            try
            {
                if (string.IsNullOrEmpty(body))
                {
                    return ApiResult.BadRequest("Body vacío");
                }

                var array = JArray.Parse(body);
                var results = new List<object>();

                foreach (var item in array)
                {
                    var json = (JObject)item;
                    var job = ParsePrintJob(json);

                    if (string.IsNullOrEmpty(job.ImpresoraIp))
                    {
                        results.Add(new { status = "ERROR", error = "impresora_ip es requerido" });
                        continue;
                    }

                    string jobId = _jobManager.Enqueue(job);
                    results.Add(new
                    {
                        status = "ENQUEUED",
                        jobId = jobId,
                        impresora = job.ImpresoraNombre ?? job.ImpresoraId,
                        ip = job.ImpresoraIp
                    });
                }

                var response = new
                {
                    total = array.Count,
                    enqueued = results.Count,
                    jobs = results
                };

                return ApiResult.Ok(JsonConvert.SerializeObject(response));
            }
            catch (JsonException ex)
            {
                Log.Error("[PRINT] Error parseando JSON array: " + ex.Message);
                return ApiResult.BadRequest("JSON inválido: " + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("[PRINT] Error procesando comandas", ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult PostVenta(string body)
        {
            // Misma lógica que PostComanda — una venta es un job de impresión
            return PostComanda(body);
        }

        public ApiResult PostPrecuenta(string body)
        {
            return PostComanda(body);
        }

        private static PrintJob ParsePrintJob(JObject json)
        {
            var job = new PrintJob();

            job.ImpresoraId = GetString(json, "impresora_id");
            job.ImpresoraIp = GetString(json, "impresora_ip") ?? GetString(json, "impresoraIP");
            job.ImpresoraNombre = GetString(json, "impresora") ?? GetString(json, "impresora_nombre");
            job.PrinterModel = GetString(json, "printermodel");
            job.ModoImpresion = GetString(json, "impresora_modoimpresion") ?? GetString(json, "modoimpresion");
            job.Contenido = GetString(json, "cadena");
            job.ContenidoHtml = GetString(json, "cadenaHTML");
            job.TipoImpresion = GetString(json, "tipo") ?? GetString(json, "tipoimpresion");
            job.DeviceIdOrigen = GetString(json, "device_id_origen");
            job.IpOrigen = GetString(json, "ip_origen");
            job.ComandaId = GetString(json, "uniqueid");

            int copias;
            string copiasStr = GetString(json, "areaproduccion_numerocopias");
            if (!string.IsNullOrEmpty(copiasStr) && int.TryParse(copiasStr, out copias) && copias > 0)
            {
                job.Copias = copias;
            }

            return job;
        }

        private static string GetString(JObject json, string key)
        {
            JToken token;
            if (json.TryGetValue(key, out token) && token.Type != JTokenType.Null)
            {
                return token.ToString();
            }
            return null;
        }
    }
}
