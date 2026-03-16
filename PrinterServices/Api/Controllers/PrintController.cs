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

        public ApiResult PostComanda(string body, string clientIp = null)
        {
            try
            {
                if (string.IsNullOrEmpty(body))
                {
                    return ApiResult.BadRequest("Body vacío");
                }

                var json = JObject.Parse(body);
                var job = ParsePrintJob(json);

                // RAZÓN: Priorizar ip_origen del JSON (viene del cliente real vía QuipuNet)
                // El clientIp del socket HTTP siempre será 127.0.0.1 porque QuipuNet y PS corren en la misma PC
                // Solo usar clientIp como fallback si el JSON no trae ip_origen
                if (string.IsNullOrEmpty(job.IpOrigen) && !string.IsNullOrEmpty(clientIp))
                {
                    job.IpOrigen = clientIp; // Fallback: usar IP del socket HTTP si JSON no trae ip_origen
                }

                if (string.IsNullOrEmpty(job.ImpresoraIp))
                {
                    return ApiResult.BadRequest("impresora_ip es requerido");
                }

                string jobId = _jobManager.Enqueue(job);

                var response = new
                {
                    status = "OK",
                    jobs = new[] { new { job_id = jobId, pedido_ids = job.PedidoIds ?? new List<string>() } }
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

        public ApiResult PostComandas(string body, string clientIp = null)
        {
            try
            {
                if (string.IsNullOrEmpty(body))
                {
                    return ApiResult.BadRequest("Body vacío");
                }

                var array = JArray.Parse(body);
                var jobResults = new List<object>();

                // RAZÓN: Deduplicación dentro del mismo batch HTTP.
                // Clave = ComandaId|ImpresoraId — si el mismo batch trae dos items idénticos,
                // el segundo se descarta. Esto NO afecta a requests separados (reprints legítimos)
                // ni al campo Copias (que se maneja por job, no por duplicar items).
                var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in array)
                {
                    var json = (JObject)item;
                    var job = ParsePrintJob(json);

                    // RAZÓN: Priorizar ip_origen del JSON (viene del cliente real vía QuipuNet)
                    // Solo usar clientIp del socket como fallback si el JSON no trae ip_origen
                    if (string.IsNullOrEmpty(job.IpOrigen) && !string.IsNullOrEmpty(clientIp))
                    {
                        job.IpOrigen = clientIp; // Fallback: usar IP del socket HTTP si JSON no trae ip_origen
                    }

                    if (string.IsNullOrEmpty(job.ImpresoraIp))
                    {
                        Log.WarnFormat("[PRINT] Job omitido: impresora_ip vacío para comanda {0}", job.ComandaId);
                        continue;
                    }

                    // RAZÓN: Verificar duplicado dentro del mismo batch por ComandaId + ImpresoraId
                    if (!string.IsNullOrEmpty(job.ComandaId) && !string.IsNullOrEmpty(job.ImpresoraId))
                    {
                        string dedupKey = job.ComandaId + "|" + job.ImpresoraId;
                        if (!seenInBatch.Add(dedupKey))
                        {
                            // Ya existe otro item en ESTE batch con mismo ComandaId + ImpresoraId → descartar
                            Log.WarnFormat("[PRINT] DUPLICADO EN BATCH descartado → ComandaId={0} ImpresoraId={1}",
                                job.ComandaId, job.ImpresoraId);
                            continue;
                        }
                    }

                    string jobId = _jobManager.Enqueue(job);
                    jobResults.Add(new
                    {
                        job_id = jobId,
                        pedido_ids = job.PedidoIds ?? new List<string>()
                    });
                }

                var response = new
                {
                    status = "OK",
                    jobs = jobResults
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

        public ApiResult PostVenta(string body, string clientIp = null)
        {
            // Misma lógica que PostComanda — una venta es un job de impresión
            return PostComanda(body, clientIp); // Pasar clientIp para trazabilidad
        }

        public ApiResult PostPrecuenta(string body, string clientIp = null)
        {
            return PostComanda(body, clientIp); // Pasar clientIp para trazabilidad
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
            job.DeviceIdOrigen = GetString(json, "device_id_origen");       // Nombre del dispositivo que originó la impresión
            job.IpOrigen = GetString(json, "ip_origen");                      // IP del cliente real que originó la impresión
            job.IpServidor = GetString(json, "ip_servidor");                  // IP del servidor QuipuNet (siempre se notifica aquí)
            job.ComandaId = GetString(json, "uniqueid");                      // ID único de la comanda

            int copias;
            string copiasStr = GetString(json, "areaproduccion_numerocopias");
            if (!string.IsNullOrEmpty(copiasStr) && int.TryParse(copiasStr, out copias) && copias > 0)
            {
                job.Copias = copias;
            }

            // Phase 2: formatting fields
            job.TamanioLetra = GetString(json, "impresora_tamanioletra");
            job.TipoGeneracion = GetString(json, "impresora_tipogeneracion");
            job.CodigoCorte = GetString(json, "codigocorte");
            job.QrData = GetString(json, "qrData") ?? GetString(json, "qrEncuesta");

            string abreGaveta = GetString(json, "abregaveta");
            job.AbreGaveta = abreGaveta == "1" || (abreGaveta != null && abreGaveta.Equals("true", StringComparison.OrdinalIgnoreCase));

            // lineasimprimir: viene como JSON array dentro del objeto
            JToken lineasToken;
            if (json.TryGetValue("lineasimprimir", out lineasToken) && lineasToken.Type == JTokenType.Array)
            {
                job.LineasImprimirJson = lineasToken.ToString();
            }

            // Fase 7A: pedido_ids y cash_drawer_code
            JToken pedidoIdsToken;
            if (json.TryGetValue("pedido_ids", out pedidoIdsToken) && pedidoIdsToken.Type == JTokenType.Array)
            {
                job.PedidoIds = pedidoIdsToken.ToObject<List<string>>();
            }

            job.CashDrawerCode = GetString(json, "cash_drawer_code");

            // RAZÓN: Capturar área de impresión (ej: "COCINA AUXILIAR", "BARRA")
            // QuipuNet envía el campo "Area" en el objeto de impresión
            job.AreaImpresion = GetString(json, "area") ?? GetString(json, "area_impresion");

            // ─── Fase 7B: Campos para ventas (FE QR), encuestas y promociones ────
            // RAZÓN: imprimirVenta() en el Front busca ##FE## en cadena y genera QR ESC/POS.
            // PS necesita estos datos para replicar el mismo renderizado en BuildPayload.
            JToken feToken;
            if (json.TryGetValue("facturacionElectronica", StringComparison.OrdinalIgnoreCase, out feToken))
            {
                job.FacturacionElectronica = feToken.Type == JTokenType.Boolean
                    ? feToken.Value<bool>()                                    // JSON boolean
                    : feToken.ToString() == "1" || feToken.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            job.TamanioQr = GetString(json, "impresora_tamanioqr");            // Tamaño QR de la impresora
            job.QrEncuesta = GetString(json, "qrEncuesta");                    // QR de encuesta (separado de QrData)

            // Copias para promociones: si viene promocionsorteo_cantidadimpresiones, usar como Copias
            // RAZÓN: imprimirPromociones() imprime N copias del sorteo. Es distinto de areaproduccion_numerocopias (comandas).
            string promoCopias = GetString(json, "promocionsorteo_cantidadimpresiones");
            if (!string.IsNullOrEmpty(promoCopias))
            {
                int pc;
                if (int.TryParse(promoCopias, out pc) && pc > 0 && pc > job.Copias)
                {
                    job.Copias = pc;                                           // Promociones: usar mayor valor de copias
                }
            }

            return job;
        }

        private static string GetString(JObject json, string key)
        {
            JToken token;
            // RAZÓN: Usar StringComparison.OrdinalIgnoreCase para que matchee "area", "Area", "AREA", etc.
            if (json.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out token) && token.Type != JTokenType.Null)
            {
                return token.ToString();
            }
            return null;
        }
    }
}
