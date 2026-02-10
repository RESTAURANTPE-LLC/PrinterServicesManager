using System;
using System.Linq;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Api.Controllers
{
    public class PrinterController
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterController));

        private readonly PrinterServiceDb _db;

        public PrinterController(PrinterServiceDb db)
        {
            _db = db;
        }

        public ApiResult GetAllPrinters()
        {
            try
            {
                var printers = _db.Query<PrinterEntity>("SELECT * FROM printers ORDER BY nombre ASC");

                var response = new
                {
                    count = printers.Count,
                    printers = printers.Select(p => new
                    {
                        impresoraId = p.ImpresoraId,
                        nombre = p.Nombre,
                        ip = p.Ip,
                        puerto = p.Puerto,
                        macAddress = p.MacAddress,
                        modelo = p.Modelo,
                        modoImpresion = p.ModoImpresion,
                        online = p.EstadoOnline == 1,
                        tienePapel = p.TienePapel == 1,
                        tapaAbierta = p.TapaAbierta == 1,
                        ultimoCheck = p.UltimoCheck
                    })
                };

                return ApiResult.Ok(JsonConvert.SerializeObject(response));
            }
            catch (Exception ex)
            {
                Log.Error("[PRINTER] Error obteniendo impresoras", ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult GetPrinterStatus(string impresoraId)
        {
            try
            {
                var printer = _db.Query<PrinterEntity>(
                    "SELECT * FROM printers WHERE impresora_id = ?", impresoraId).FirstOrDefault();

                if (printer == null)
                {
                    return ApiResult.NotFound();
                }

                var response = new
                {
                    impresoraId = printer.ImpresoraId,
                    nombre = printer.Nombre,
                    ip = printer.Ip,
                    puerto = printer.Puerto,
                    macAddress = printer.MacAddress,
                    modelo = printer.Modelo,
                    online = printer.EstadoOnline == 1,
                    tienePapel = printer.TienePapel == 1,
                    tapaAbierta = printer.TapaAbierta == 1,
                    ipResueltaPorArp = printer.IpResueltaPorArp == 1,
                    ultimoCheck = printer.UltimoCheck
                };

                return ApiResult.Ok(JsonConvert.SerializeObject(response));
            }
            catch (Exception ex)
            {
                Log.Error("[PRINTER] Error obteniendo status de " + impresoraId, ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult RegisterPrinter(string body)
        {
            try
            {
                if (string.IsNullOrEmpty(body))
                {
                    return ApiResult.BadRequest("Body vacío");
                }

                var json = JObject.Parse(body);

                string impresoraId = GetString(json, "impresora_id");
                if (string.IsNullOrEmpty(impresoraId))
                {
                    return ApiResult.BadRequest("impresora_id es requerido");
                }

                string ip = GetString(json, "ip") ?? GetString(json, "impresora_ip");
                if (string.IsNullOrEmpty(ip))
                {
                    return ApiResult.BadRequest("ip es requerido");
                }

                // Verificar si ya existe
                var existing = _db.Query<PrinterEntity>(
                    "SELECT * FROM printers WHERE impresora_id = ?", impresoraId).FirstOrDefault();

                if (existing != null)
                {
                    // Actualizar
                    existing.Ip = ip;
                    existing.Nombre = GetString(json, "nombre") ?? existing.Nombre;
                    existing.Modelo = GetString(json, "modelo") ?? GetString(json, "printermodel") ?? existing.Modelo;
                    existing.ModoImpresion = GetString(json, "modo_impresion") ?? existing.ModoImpresion;
                    existing.MacAddress = GetString(json, "mac_address") ?? existing.MacAddress;

                    int puerto;
                    string puertoStr = GetString(json, "puerto");
                    if (!string.IsNullOrEmpty(puertoStr) && int.TryParse(puertoStr, out puerto))
                    {
                        existing.Puerto = puerto;
                    }

                    _db.Update(existing);

                    Log.InfoFormat("[PRINTER] Impresora actualizada: {0} ({1}) en {2}", impresoraId, existing.Nombre, ip);
                    return ApiResult.Ok(JsonConvert.SerializeObject(new { status = "UPDATED", impresoraId = impresoraId }));
                }
                else
                {
                    // Crear nueva
                    var printer = new PrinterEntity
                    {
                        ImpresoraId = impresoraId,
                        Nombre = GetString(json, "nombre") ?? impresoraId,
                        Ip = ip,
                        Modelo = GetString(json, "modelo") ?? GetString(json, "printermodel"),
                        ModoImpresion = GetString(json, "modo_impresion"),
                        MacAddress = GetString(json, "mac_address"),
                        FechaRegistro = DateTime.Now.ToString("o")
                    };

                    int puerto;
                    string puertoStr = GetString(json, "puerto");
                    if (!string.IsNullOrEmpty(puertoStr) && int.TryParse(puertoStr, out puerto))
                    {
                        printer.Puerto = puerto;
                    }

                    _db.Insert(printer);

                    Log.InfoFormat("[PRINTER] Impresora registrada: {0} ({1}) en {2}", impresoraId, printer.Nombre, ip);
                    return ApiResult.Ok(JsonConvert.SerializeObject(new { status = "REGISTERED", impresoraId = impresoraId }));
                }
            }
            catch (Exception ex)
            {
                Log.Error("[PRINTER] Error registrando impresora", ex);
                return ApiResult.Error(ex.Message);
            }
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
