using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Transport;

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
                        ultimoCheck = p.UltimoCheck,
                        tipoConexion = p.TipoConexion ?? "RED",
                        usbUniqueKey = p.UsbUniqueKey,
                        usbDevicePath = p.UsbDevicePath,
                        usbFriendlyName = p.UsbFriendlyName
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

        /// <summary>
        /// PUT /api/printer/update — Editar impresora desde dashboard.
        /// Solo actualiza: nombre, ip, puerto, modelo.
        /// MAC NO se modifica (es identificador único de hardware).
        /// IP solo puede ser sobreescrita por la capa de red si confirma online.
        /// </summary>
        public ApiResult UpdatePrinter(string body)
        {
            try
            {
                var json = JObject.Parse(body);
                string impresoraId = json.Value<string>("impresoraId");

                if (string.IsNullOrEmpty(impresoraId))
                {
                    return ApiResult.BadRequest("impresoraId es requerido");
                }

                var printer = _db.Table<PrinterEntity>()
                    .FirstOrDefault(p => p.ImpresoraId == impresoraId);

                if (printer == null)
                {
                    return ApiResult.NotFound();
                }

                // Actualizar solo campos editables (MAC NUNCA se toca)
                if (json["nombre"] != null)
                    printer.Nombre = json.Value<string>("nombre");
                if (json["ip"] != null)
                    printer.Ip = json.Value<string>("ip");
                if (json["puerto"] != null)
                    printer.Puerto = json.Value<int>("puerto");
                if (json["modelo"] != null)
                    printer.Modelo = json.Value<string>("modelo");

                _db.Update(printer);

                Log.InfoFormat("======[ PRINTER EDIT ]====== {0} actualizada desde dashboard: ip={1} puerto={2} nombre={3}",
                    impresoraId, printer.Ip, printer.Puerto, printer.Nombre);

                return ApiResult.Ok(JsonConvert.SerializeObject(new
                {
                    status = "UPDATED",
                    impresoraId = printer.ImpresoraId,
                    nombre = printer.Nombre,
                    ip = printer.Ip,
                    puerto = printer.Puerto,
                    modelo = printer.Modelo,
                    macAddress = printer.MacAddress
                }));
            }
            catch (Exception ex)
            {
                Log.Error("[PRINTER] Error actualizando impresora: " + ex.Message, ex);
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
                    ultimoCheck = printer.UltimoCheck,
                    tipoConexion = printer.TipoConexion ?? "RED",
                    usbUniqueKey = printer.UsbUniqueKey,
                    usbDevicePath = printer.UsbDevicePath,
                    usbFriendlyName = printer.UsbFriendlyName
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

                string tipoConexion = GetString(json, "tipo_conexion") ?? "RED";
                string usbUniqueKey = GetString(json, "usb_unique_key");

                string ip = GetString(json, "ip") ?? GetString(json, "impresora_ip");
                // IP es requerido solo para impresoras de RED
                // Impresoras USB usan usb_unique_key como identificador
                if (string.IsNullOrEmpty(ip) && tipoConexion != "USB")
                {
                    return ApiResult.BadRequest("ip es requerido para impresoras de red");
                }
                if (tipoConexion == "USB" && string.IsNullOrEmpty(usbUniqueKey))
                {
                    return ApiResult.BadRequest("usb_unique_key es requerido para impresoras USB");
                }

                // Verificar si ya existe
                var existing = _db.Query<PrinterEntity>(
                    "SELECT * FROM printers WHERE impresora_id = ?", impresoraId).FirstOrDefault();

                if (existing != null)
                {
                    // Actualizar — IP solo si la impresora está OFFLINE (misma regla que SyncPrinters)
                    if (existing.EstadoOnline != 1 && !string.IsNullOrEmpty(ip))
                    {
                        existing.Ip = ip;
                    }
                    else if (!string.IsNullOrEmpty(ip) && ip != existing.Ip)
                    {
                        Log.InfoFormat("======[ REGISTER IP IGNORADA ]====== {0} está ONLINE en {1}, request envió {2}",
                            existing.Nombre ?? existing.ImpresoraId, existing.Ip, ip);
                    }
                    existing.Nombre = GetString(json, "nombre") ?? existing.Nombre;
                    existing.Modelo = GetString(json, "modelo") ?? GetString(json, "printermodel") ?? existing.Modelo;
                    existing.ModoImpresion = GetString(json, "modo_impresion") ?? existing.ModoImpresion;
                    // MAC solo si no tiene una aún
                    string newMac = GetString(json, "mac_address");
                    if (string.IsNullOrEmpty(existing.MacAddress) && !string.IsNullOrEmpty(newMac))
                    {
                        existing.MacAddress = newMac;
                    }

                    // Campos USB
                    existing.TipoConexion = tipoConexion;
                    if (!string.IsNullOrEmpty(usbUniqueKey))
                    {
                        existing.UsbUniqueKey = usbUniqueKey;
                        existing.UsbDevicePath = GetString(json, "usb_device_path") ?? existing.UsbDevicePath;
                        existing.UsbFriendlyName = GetString(json, "usb_friendly_name") ?? existing.UsbFriendlyName;
                    }

                    int puerto;
                    string puertoStr = GetString(json, "puerto");
                    if (!string.IsNullOrEmpty(puertoStr) && int.TryParse(puertoStr, out puerto))
                    {
                        existing.Puerto = puerto;
                    }

                    // Dedup por MAC: si otra impresora ya tiene la misma MAC, es el mismo dispositivo físico
                    // → actualizar los datos de la impresora existente (no duplicar, no eliminar)
                    if (!string.IsNullOrEmpty(existing.MacAddress))
                    {
                        var macDuplicate = _db.Query<PrinterEntity>(
                            "SELECT * FROM printers WHERE mac_address = ? AND impresora_id != ?",
                            existing.MacAddress, impresoraId).FirstOrDefault();
                        if (macDuplicate != null)
                        {
                            // Actualizar la impresora que ya tiene esa MAC — IP solo si está OFFLINE
                            if (macDuplicate.EstadoOnline != 1 && !string.IsNullOrEmpty(ip))
                            {
                                macDuplicate.Ip = ip;
                            }
                            macDuplicate.Nombre = GetString(json, "nombre") ?? macDuplicate.Nombre;
                            macDuplicate.Modelo = GetString(json, "modelo") ?? GetString(json, "printermodel") ?? macDuplicate.Modelo;
                            macDuplicate.ModoImpresion = GetString(json, "modo_impresion") ?? macDuplicate.ModoImpresion;
                            if (existing.Puerto > 0) macDuplicate.Puerto = existing.Puerto;
                            _db.Update(macDuplicate);
                            // Quitar la MAC del registro actual para evitar duplicado
                            existing.MacAddress = null;
                            _db.Update(existing);
                            Log.WarnFormat("[PRINTER] MAC {0} ya registrada en {1} ({2}) → datos actualizados. Se quitó MAC de {3}",
                                macDuplicate.MacAddress, macDuplicate.ImpresoraId, macDuplicate.Nombre, impresoraId);
                            return ApiResult.Ok(JsonConvert.SerializeObject(new { status = "UPDATED_BY_MAC", impresoraId = macDuplicate.ImpresoraId }));
                        }
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
                        Ip = ip ?? "",
                        Modelo = GetString(json, "modelo") ?? GetString(json, "printermodel"),
                        ModoImpresion = GetString(json, "modo_impresion"),
                        MacAddress = GetString(json, "mac_address"),
                        FechaRegistro = DateTime.Now.ToString("o"),
                        TipoConexion = tipoConexion,
                        UsbUniqueKey = usbUniqueKey,
                        UsbDevicePath = GetString(json, "usb_device_path"),
                        UsbFriendlyName = GetString(json, "usb_friendly_name")
                    };

                    int puerto;
                    string puertoStr = GetString(json, "puerto");
                    if (!string.IsNullOrEmpty(puertoStr) && int.TryParse(puertoStr, out puerto))
                    {
                        printer.Puerto = puerto;
                    }

                    // Dedup por MAC: si otra impresora ya tiene la misma MAC, es el mismo dispositivo físico
                    // → actualizar los datos de la impresora existente (no duplicar, no eliminar)
                    if (!string.IsNullOrEmpty(printer.MacAddress))
                    {
                        var macDuplicate = _db.Query<PrinterEntity>(
                            "SELECT * FROM printers WHERE mac_address = ? AND impresora_id != ?",
                            printer.MacAddress, impresoraId).FirstOrDefault();
                        if (macDuplicate != null)
                        {
                            // Actualizar la impresora que ya tiene esa MAC — IP solo si está OFFLINE
                            if (macDuplicate.EstadoOnline != 1 && !string.IsNullOrEmpty(ip))
                            {
                                macDuplicate.Ip = ip;
                            }
                            macDuplicate.Nombre = printer.Nombre ?? macDuplicate.Nombre;
                            macDuplicate.Modelo = printer.Modelo ?? macDuplicate.Modelo;
                            macDuplicate.ModoImpresion = printer.ModoImpresion ?? macDuplicate.ModoImpresion;
                            if (printer.Puerto > 0) macDuplicate.Puerto = printer.Puerto;
                            _db.Update(macDuplicate);
                            Log.WarnFormat("[PRINTER] MAC {0} ya registrada en {1} ({2}) → datos actualizados con info de {3}",
                                macDuplicate.MacAddress, macDuplicate.ImpresoraId, macDuplicate.Nombre, impresoraId);
                            return ApiResult.Ok(JsonConvert.SerializeObject(new { status = "UPDATED_BY_MAC", impresoraId = macDuplicate.ImpresoraId }));
                        }
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
                int skippedDupMac = 0; // Contador de impresoras omitidas por MAC duplicada en batch

                // ═══════════════════════════════════════════════════════════════════════════════
                // PRE-PROCESO: Consolidar duplicados por MAC DENTRO del batch
                // RAZÓN: QuipuNetX puede enviar múltiples impresoras lógicas con la misma MAC
                // (ej: "PRINTER 1" y "PRINTER1" apuntando al mismo dispositivo físico).
                // Quedarse solo con la PRIMERA por cada MAC normalizada.
                // ═══════════════════════════════════════════════════════════════════════════════
                var seenMacs = new Dictionary<string, string>(); // MAC normalizada → impresora_id ganadora
                var dedupedPrinters = new List<PrinterSyncDto>();

                foreach (var dto in printers)
                {
                    if (string.IsNullOrEmpty(dto.mac_address))
                    {
                        // Sin MAC: incluir siempre (se enriquecerá luego vía ARP)
                        dedupedPrinters.Add(dto);
                        continue;
                    }

                    string normalizedMac = MacAddressNormalizer.Normalize(dto.mac_address);
                    if (string.IsNullOrEmpty(normalizedMac))
                    {
                        dedupedPrinters.Add(dto);
                        continue;
                    }

                    if (seenMacs.ContainsKey(normalizedMac))
                    {
                        // MAC ya vista en este batch → omitir duplicado
                        Log.WarnFormat("[PRINTER-SYNC] Duplicado por MAC en batch: {0} ({1}) tiene misma MAC {2} que {3} → omitiendo",
                            dto.impresora_id, dto.nombre, MacAddressNormalizer.Format(normalizedMac), seenMacs[normalizedMac]);
                        skippedDupMac++;
                        continue;
                    }

                    seenMacs[normalizedMac] = dto.impresora_id;
                    dedupedPrinters.Add(dto);
                }

                if (skippedDupMac > 0)
                {
                    Log.WarnFormat("[PRINTER-SYNC] ⚠ {0} impresora(s) omitida(s) por MAC duplicada en batch", skippedDupMac);
                }

                // Procesar cada impresora del batch ya deduplicado
                foreach (var dto in dedupedPrinters)
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
                        // IP: Solo actualizar si la impresora está OFFLINE en PrinterServices.
                        // Si está ONLINE, la IP actual fue confirmada por la capa de red (StatusMonitor/ARP)
                        // y es más confiable que la IP que QuipuNet envía (puede estar desactualizada).
                        if (existing.EstadoOnline != 1 && !string.IsNullOrEmpty(dto.ip))
                        {
                            existing.Ip = dto.ip;
                        }
                        else if (!string.IsNullOrEmpty(dto.ip) && dto.ip != existing.Ip)
                        {
                            Log.InfoFormat("======[ SYNC IP IGNORADA ]====== {0} está ONLINE en {1}, QuipuNet envió {2} — se mantiene IP actual",
                                existing.Nombre ?? existing.ImpresoraId, existing.Ip, dto.ip);
                        }
                        existing.Nombre = dto.nombre ?? existing.Nombre; // Actualizar nombre (si viene)
                        existing.Puerto = dto.puerto > 0 ? dto.puerto : existing.Puerto; // Actualizar puerto (si viene > 0)
                        // MAC: Solo actualizar si la impresora NO tiene MAC aún (primera vez).
                        // Una vez asignada, la MAC es inmutable (identificador de hardware).
                        if (string.IsNullOrEmpty(existing.MacAddress) && !string.IsNullOrEmpty(dto.mac_address))
                        {
                            existing.MacAddress = dto.mac_address;
                        }
                        // NOTA: NO actualizar estado online/offline - eso lo maneja StatusMonitor

                        // Dedup por MAC: si otra impresora ya tiene la misma MAC, es el mismo dispositivo físico
                        // → actualizar los datos de la impresora existente (no duplicar, no eliminar)
                        if (!string.IsNullOrEmpty(existing.MacAddress))
                        {
                            var macDuplicate = _db.Query<PrinterEntity>(
                                "SELECT * FROM printers WHERE mac_address = ? AND impresora_id != ?",
                                existing.MacAddress, dto.impresora_id).FirstOrDefault();
                            if (macDuplicate != null)
                            {
                                // Actualizar la impresora que ya tiene esa MAC — IP solo si está OFFLINE
                                if (macDuplicate.EstadoOnline != 1 && !string.IsNullOrEmpty(dto.ip))
                                {
                                    macDuplicate.Ip = dto.ip;
                                }
                                macDuplicate.Nombre = dto.nombre ?? macDuplicate.Nombre;
                                macDuplicate.Puerto = dto.puerto > 0 ? dto.puerto : macDuplicate.Puerto;
                                _db.Update(macDuplicate);
                                // Quitar la MAC del registro actual para evitar duplicado
                                existing.MacAddress = null;
                                _db.Update(existing);
                                Log.WarnFormat("[PRINTER-SYNC] MAC {0} ya registrada en {1} ({2}) → datos actualizados. Se quitó MAC de {3}",
                                    macDuplicate.MacAddress, macDuplicate.ImpresoraId, macDuplicate.Nombre, dto.impresora_id);
                                updatedCount++;
                                continue; // Ya se procesó, saltar al siguiente
                            }
                        }

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

                        // Dedup por MAC: si otra impresora ya tiene la misma MAC, es el mismo dispositivo físico
                        // → actualizar los datos de la impresora existente (no duplicar, no eliminar)
                        if (!string.IsNullOrEmpty(newPrinter.MacAddress))
                        {
                            var macDuplicate = _db.Query<PrinterEntity>(
                                "SELECT * FROM printers WHERE mac_address = ? AND impresora_id != ?",
                                newPrinter.MacAddress, dto.impresora_id).FirstOrDefault();
                            if (macDuplicate != null)
                            {
                                // Actualizar la impresora que ya tiene esa MAC — IP solo si está OFFLINE
                                if (macDuplicate.EstadoOnline != 1 && !string.IsNullOrEmpty(dto.ip))
                                {
                                    macDuplicate.Ip = dto.ip;
                                }
                                macDuplicate.Nombre = dto.nombre ?? macDuplicate.Nombre;
                                macDuplicate.Puerto = dto.puerto > 0 ? dto.puerto : macDuplicate.Puerto;
                                _db.Update(macDuplicate);
                                Log.WarnFormat("[PRINTER-SYNC] MAC {0} ya registrada en {1} ({2}) → datos actualizados con info de {3}",
                                    macDuplicate.MacAddress, macDuplicate.ImpresoraId, macDuplicate.Nombre, dto.impresora_id);
                                updatedCount++;
                                continue; // No insertar, ya se actualizó la existente
                            }
                        }

                        _db.Insert(newPrinter); // Ejecutar INSERT en BD
                        insertedCount++; // Incrementar contador de insertados

                        Log.InfoFormat("[PRINTER-SYNC] Insertada: {0} ({1}) → {2}", 
                            dto.impresora_id, dto.nombre, dto.ip); // Log de inserción
                    }
                }

                // Crear respuesta exitosa con estadísticas
                var response = SyncResponseDto.Success(insertedCount, updatedCount);

                Log.InfoFormat("[PRINTER-SYNC] ✅ Sincronización completada: {0} impresoras ({1} nuevas, {2} actualizadas, {3} omitidas por MAC duplicada)", 
                    response.synchronized, insertedCount, updatedCount, skippedDupMac); // Log de resumen

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

        /// <summary>
        /// Descubre impresoras USB conectadas al equipo.
        /// Análogo a un "ARP scan" pero para dispositivos USB.
        /// Retorna VID, PID, Serial, DevicePath y FriendlyName de cada impresora encontrada.
        /// El Front puede usar esta info para registrar impresoras USB (POST /api/printer/register).
        /// </summary>
        public ApiResult DiscoverUsbPrinters()
        {
            try
            {
                var devices = UsbDeviceEnumerator.EnumerateUsbPrinters();

                var response = new
                {
                    count = devices.Count,
                    printers = devices.ConvertAll(d => new
                    {
                        vid = d.Vid,
                        pid = d.Pid,
                        serialNumber = d.SerialNumber,
                        uniqueKey = d.UniqueKey,
                        devicePath = d.DevicePath,
                        friendlyName = d.FriendlyName
                    })
                };

                Log.InfoFormat("[PRINTER-USB] Descubrimiento USB: {0} impresora(s) encontrada(s)", devices.Count);
                return ApiResult.Ok(JsonConvert.SerializeObject(response));
            }
            catch (Exception ex)
            {
                Log.Error("[PRINTER-USB] Error descubriendo impresoras USB", ex);
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
