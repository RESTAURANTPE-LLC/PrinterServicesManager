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

        /// <summary>
        /// Sincroniza lista de impresoras desde QuipuNetX.
        /// QuipuNetX envía su catálogo completo al iniciar el servidor.
        /// Este endpoint inserta nuevas impresoras o actualiza las existentes.
        /// </summary>
        /// <param name="body">JSON array con lista de PrinterSyncDto</param>
        /// <returns>SyncResponseDto con resultado de sincronización</returns>
        public ApiResult SyncPrinters(string body)
        {
            try
            {
                // Validar que el body no esté vacío
                if (string.IsNullOrEmpty(body))
                {
                    return ApiResult.BadRequest("Body vacío"); // Retornar error 400
                }

                // Deserializar JSON array a lista de DTOs
                var printers = JsonConvert.DeserializeObject<System.Collections.Generic.List<PrinterSyncDto>>(body);

                // Validar que el array no sea null o vacío
                if (printers == null || printers.Count == 0)
                {
                    return ApiResult.BadRequest("Lista de impresoras vacía"); // Retornar error 400
                }

                int insertedCount = 0; // Contador de impresoras nuevas insertadas
                int updatedCount = 0;  // Contador de impresoras existentes actualizadas

                // Procesar cada impresora en el array
                foreach (var dto in printers)
                {
                    // Validar campos obligatorios
                    if (string.IsNullOrEmpty(dto.impresora_id))
                    {
                        Log.WarnFormat("[PRINTER-SYNC] Impresora sin ID, ignorando"); // Loguear warning
                        continue; // Saltar a la siguiente impresora
                    }

                    if (string.IsNullOrEmpty(dto.ip))
                    {
                        Log.WarnFormat("[PRINTER-SYNC] Impresora {0} sin IP, ignorando", dto.impresora_id); // Loguear warning
                        continue; // Saltar a la siguiente impresora
                    }

                    // Verificar si la impresora ya existe en la BD (por impresora_id)
                    var existing = _db.Query<PrinterEntity>(
                        "SELECT * FROM printers WHERE impresora_id = ?", dto.impresora_id).FirstOrDefault();

                    if (existing != null)
                    {
                        // ═══════════════════════════════════════════════════════
                        // ACTUALIZAR impresora existente
                        // ═══════════════════════════════════════════════════════
                        existing.Ip = dto.ip; // Actualizar IP
                        existing.Nombre = dto.nombre ?? existing.Nombre; // Actualizar nombre (si viene)
                        existing.Puerto = dto.puerto > 0 ? dto.puerto : existing.Puerto; // Actualizar puerto (si viene > 0)
                        existing.MacAddress = dto.mac_address ?? existing.MacAddress; // Actualizar MAC (si viene)
                        // NOTA: NO actualizar estado online/offline - eso lo maneja StatusMonitor

                        _db.Update(existing); // Ejecutar UPDATE en BD
                        updatedCount++; // Incrementar contador de actualizados

                        Log.DebugFormat("[PRINTER-SYNC] Actualizada: {0} ({1}) → {2}", 
                            dto.impresora_id, dto.nombre, dto.ip); // Log de actualización
                    }
                    else
                    {
                        // ═══════════════════════════════════════════════════════
                        // INSERTAR impresora nueva
                        // ═══════════════════════════════════════════════════════
                        var newPrinter = new PrinterEntity
                        {
                            ImpresoraId = dto.impresora_id, // Asignar ID
                            Nombre = dto.nombre ?? dto.impresora_id, // Asignar nombre (default = ID)
                            Ip = dto.ip, // Asignar IP
                            Puerto = dto.puerto > 0 ? dto.puerto : 9100, // Puerto (default 9100)
                            MacAddress = dto.mac_address, // Asignar MAC
                            ModoImpresion = "ethernet", // Default: ethernet (QuipuNetX envía solo modo SERVICIO = ethernet)
                            FechaRegistro = DateTime.Now.ToString("o"), // Timestamp ISO 8601
                            EstadoOnline = 0, // Default: offline (StatusMonitor lo actualizará)
                            TienePapel = 1, // Default: asumimos que tiene papel
                            TapaAbierta = 0, // Default: asumimos tapa cerrada
                            IpResueltaPorArp = 0 // Default: no resuelta por ARP
                        };

                        _db.Insert(newPrinter); // Ejecutar INSERT en BD
                        insertedCount++; // Incrementar contador de insertados

                        Log.InfoFormat("[PRINTER-SYNC] Insertada: {0} ({1}) → {2}", 
                            dto.impresora_id, dto.nombre, dto.ip); // Log de inserción
                    }
                }

                // Crear respuesta exitosa con estadísticas
                var response = SyncResponseDto.Success(insertedCount, updatedCount);

                Log.InfoFormat("[PRINTER-SYNC] ✅ Sincronización completada: {0} impresoras ({1} nuevas, {2} actualizadas)", 
                    response.synchronized, insertedCount, updatedCount); // Log de resumen

                // Serializar DTO a JSON y retornar 200 OK
                return ApiResult.Ok(JsonConvert.SerializeObject(response));
            }
            catch (JsonException jsonEx)
            {
                // Error de deserialización JSON
                Log.Error("[PRINTER-SYNC] Error deserializando JSON", jsonEx);
                var errorResponse = SyncResponseDto.Failure("JSON inválido: " + jsonEx.Message);
                return ApiResult.BadRequest(JsonConvert.SerializeObject(errorResponse)); // Retornar 400
            }
            catch (Exception ex)
            {
                // Error inesperado
                Log.Error("[PRINTER-SYNC] Error durante sincronización", ex);
                var errorResponse = SyncResponseDto.Failure(ex.Message);
                return ApiResult.Error(JsonConvert.SerializeObject(errorResponse)); // Retornar 500
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
