using System;
using log4net;
using Newtonsoft.Json;
using PrinterServices.Core.Network;

namespace PrinterServices.Api.Controllers
{
    /// <summary>
    /// Controller para operaciones de red avanzadas.
    /// Incluye cambio de IP de impresoras ESC/POS en segmentos de red diferentes.
    /// </summary>
    public class NetworkController
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkController)); // Logger log4net

        /// <summary>
        /// GET /api/printer/resetip?ipactual=X&ipfinal=Y[&mask=Z&gateway=W&community=C]
        /// Cambia la IP de una impresora ESC/POS que está en otro segmento de red.
        /// Crea subred temporal, envía comando de cambio vía SNMP/TCP, obtiene MAC, limpia.
        /// </summary>
        /// <param name="ipActual">IP actual de la impresora (ej: 192.168.1.23).</param>
        /// <param name="ipFinal">IP nueva deseada (ej: 192.168.68.65).</param>
        /// <param name="subnetMask">Máscara de subred (opcional, default: 255.255.255.0).</param>
        /// <param name="gateway">Gateway para la nueva IP (opcional, auto-detecta del segmento destino).</param>
        /// <param name="snmpCommunity">Community SNMP de escritura (opcional, default: "private").</param>
        /// <returns>ApiResult con JSON que incluye éxito/error, MAC, método usado, y log de pasos.</returns>
        public ApiResult ResetIp(string ipActual, string ipFinal, string subnetMask, string gateway, string snmpCommunity)
        {
            try
            {
                // Validar parámetros obligatorios
                if (string.IsNullOrEmpty(ipActual)) // IP actual es requerida
                {
                    return ApiResult.BadRequest("Parámetro 'ipactual' es requerido"); // Error 400
                }
                if (string.IsNullOrEmpty(ipFinal)) // IP final es requerida
                {
                    return ApiResult.BadRequest("Parámetro 'ipfinal' es requerido"); // Error 400
                }

                // Valores por defecto para parámetros opcionales
                if (string.IsNullOrEmpty(subnetMask)) // Si no se especificó máscara
                {
                    subnetMask = "255.255.255.0"; // Default: clase C
                }
                if (string.IsNullOrEmpty(snmpCommunity)) // Si no se especificó community
                {
                    snmpCommunity = "private"; // Default: community de escritura estándar SNMP
                }

                Log.InfoFormat("[RESETIP] Solicitud de cambio de IP: {0} → {1} (mask={2}, gw={3}, community={4})",
                    ipActual, ipFinal, subnetMask, gateway ?? "auto", snmpCommunity); // Log de solicitud

                // Ejecutar cambio de IP (operación completa: alias → MAC → SNMP/TCP → verificar → limpiar)
                var result = PrinterIpChanger.ChangeIp(
                    ipActual,       // IP actual de la impresora
                    ipFinal,        // IP nueva deseada
                    subnetMask,     // Máscara de subred
                    gateway,        // Gateway (null = auto-detectar)
                    snmpCommunity   // Community SNMP de escritura
                );

                // Construir respuesta JSON con toda la información
                var response = new
                {
                    success = result.Success,           // true si se cambió la IP
                    ipActual = result.IpActual,         // IP original
                    ipFinal = result.IpFinal,           // IP nueva
                    macAddress = result.MacAddress,     // MAC del dispositivo
                    subnetMask = result.SubnetMask,     // Máscara aplicada
                    gateway = result.Gateway,           // Gateway aplicado
                    metodo = result.Metodo,             // Método: SNMP_EPSON, SNMP_GENERIC, RAW_TCP_9100
                    fabricante = result.Fabricante,     // Fabricante detectado
                    error = result.Error,               // Mensaje de error (null si éxito)
                    pasos = result.Pasos,               // Log detallado de cada paso
                    tiempoMs = result.TiempoMs          // Tiempo total en ms
                };

                string json = JsonConvert.SerializeObject(response, Formatting.Indented); // Serializar con indentado

                if (result.Success) // Operación exitosa
                {
                    Log.InfoFormat("[RESETIP] ✓ IP cambiada: {0} → {1} (MAC={2}, método={3}, {4}ms)",
                        ipActual, ipFinal, result.MacAddress, result.Metodo, result.TiempoMs); // Log éxito
                    return ApiResult.Ok(json); // Retornar 200 OK
                }
                else // Operación falló
                {
                    Log.WarnFormat("[RESETIP] ✗ Error cambiando IP {0} → {1}: {2}",
                        ipActual, ipFinal, result.Error); // Log warning
                    // Retornar 200 con success=false (no es error del servidor, es del dispositivo)
                    return ApiResult.Ok(json); // 200 con success=false para que el cliente maneje
                }
            }
            catch (Exception ex) // Error inesperado
            {
                Log.Error("[RESETIP] Error inesperado en endpoint resetip", ex); // Log error
                return ApiResult.Error("Error interno: " + ex.Message); // Retornar 500
            }
        }
    }
}
