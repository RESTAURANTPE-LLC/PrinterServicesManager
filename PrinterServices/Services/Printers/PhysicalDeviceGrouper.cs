using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using PrinterServices.Core.Network;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Printers
{
    /// <summary>
    /// Servicio para agrupar impresoras por dispositivo físico según principio SRP.
    /// RESPONSABILIDAD ÚNICA: Identificar qué impresoras representan el mismo dispositivo físico.
    /// NO hace: Enriquecimiento de MAC, verificación de estado, propagación de estado.
    /// </summary>
    public class PhysicalDeviceGrouper : IPhysicalDeviceGrouper
    {
        // Logger para diagnóstico de agrupamiento
        // RAZÓN: Rastrear cuántas impresoras lógicas se agrupan en dispositivos físicos
        private static readonly ILog Log = LogManager.GetLogger(typeof(PhysicalDeviceGrouper));

        /// <summary>
        /// Agrupa impresoras por dispositivo físico usando MAC normalizada como identificador único.
        /// ALGORITMO:
        /// 1. Impresoras CON MAC → agrupar por MAC normalizada
        /// 2. Impresoras SIN MAC → agrupar por IP (fallback temporal)
        /// 3. Seleccionar UN representante por grupo (el primero encontrado)
        /// </summary>
        /// <param name="printers">Lista completa de impresoras registradas en BD</param>
        /// <returns>Diccionario con clave=identificador único, valor=impresora representante</returns>
        public Dictionary<string, PrinterEntity> GroupByPhysicalDevice(List<PrinterEntity> printers)
        {
            // Validar entrada no nula
            // RAZÓN: Prevenir NullReferenceException en foreach
            if (printers == null)
            {
                Log.Warn("[GROUPER] Lista de impresoras es null, retornando diccionario vacío");
                return new Dictionary<string, PrinterEntity>(); // Retornar vacío en vez de null (Null Object Pattern)
            }

            // Crear diccionario para resultado
            // RAZÓN: Dictionary<K,V> permite acceso O(1) por clave, eficiente para lookups
            var uniqueDevices = new Dictionary<string, PrinterEntity>();

            // Contadores para estadísticas de agrupamiento
            // RAZÓN: Diagnóstico de cuántos dispositivos físicos vs registros lógicos
            int totalPrinters = printers.Count;
            int withMac = 0;      // Cuántas tienen MAC (identificador físico confiable)
            int withoutMac = 0;   // Cuántas NO tienen MAC (fallback por IP)

            // Iterar sobre todas las impresoras registradas
            // RAZÓN: Necesitamos procesar cada registro para agrupar
            foreach (var printer in printers)
            {
                // Generar clave de agrupamiento
                // ESTRATEGIA: Preferir MAC (identificador físico) sobre IP (puede cambiar por DHCP)
                // RAZÓN: Una misma impresora física puede tener múltiples IPs en el tiempo
                string groupKey = GetGroupingKey(printer, out bool usingMac);

                // Si no se pudo generar clave (impresora inválida), omitir
                // RAZÓN: No agrupar impresoras sin identificadores
                if (string.IsNullOrEmpty(groupKey))
                {
                    Log.WarnFormat("[GROUPER] Impresora {0} sin clave de agrupamiento (sin MAC ni IP) - omitiendo",
                        printer.Nombre ?? printer.ImpresoraId);
                    continue;
                }

                // Actualizar estadísticas
                // RAZÓN: Para logging final de diagnóstico
                if (usingMac)
                {
                    withMac++; // Impresora con MAC (identificador confiable)
                }
                else
                {
                    withoutMac++; // Impresora sin MAC (fallback por IP)
                }

                // Solo guardar el PRIMER registro encontrado para cada dispositivo físico
                // RAZÓN: Si BARRA y BARRA 2 tienen misma MAC, solo necesitamos verificar UNA VEZ
                // PRINCIPIO: Evitar overhead de red redundante (SNMP/DLE EOT duplicados)
                if (!uniqueDevices.ContainsKey(groupKey))
                {
                    // Agregar como representante de este dispositivo físico
                    // RAZÓN: El primer registro será el que se monitoreará activamente
                    uniqueDevices[groupKey] = printer;

                    // Loguear para diagnóstico si hay múltiples registros con misma MAC
                    // RAZÓN: Detectar casos como BARRA + BARRA 2 = mismo dispositivo
                    Log.DebugFormat("[GROUPER] Dispositivo físico registrado: {0} (clave={1}, usaMac={2})",
                        printer.Nombre ?? printer.ImpresoraId,
                        usingMac ? MacAddressNormalizer.Format(groupKey) : groupKey, // Formatear MAC para legibilidad
                        usingMac);
                }
                else
                {
                    // Ya existe representante para este dispositivo físico
                    // RAZÓN: BARRA 2 comparte MAC con BARRA, no necesita monitoreo independiente
                    var existingRepresentative = uniqueDevices[groupKey];

                    // Loguear agrupamiento para diagnóstico
                    // RAZÓN: Rastrear cuándo se detectan impresoras lógicas duplicadas
                    Log.DebugFormat("[GROUPER] Impresora {0} comparte dispositivo físico con {1} (clave={2})",
                        printer.Nombre ?? printer.ImpresoraId,
                        existingRepresentative.Nombre ?? existingRepresentative.ImpresoraId,
                        usingMac ? MacAddressNormalizer.Format(groupKey) : groupKey);
                }
            }

            // Loguear estadísticas finales de agrupamiento
            // RAZÓN: Diagnóstico de efectividad del agrupamiento (reducción de overhead)
            int reduction = totalPrinters - uniqueDevices.Count; // Cuántos registros se evitaron monitorear
            Log.InfoFormat("[GROUPER] Agrupamiento completado: {0} registro(s) → {1} dispositivo(s) físico(s) (reducción: {2}, conMAC: {3}, sinMAC: {4})",
                totalPrinters,
                uniqueDevices.Count,
                reduction,
                withMac,
                withoutMac);

            // Retornar diccionario de dispositivos únicos
            // RAZÓN: StatusMonitor solo monitoreará estos representantes
            return uniqueDevices;
        }

        /// <summary>
        /// Genera clave de agrupamiento para una impresora.
        /// ESTRATEGIA: Preferir MAC normalizada > IP > null
        /// </summary>
        /// <param name="printer">Impresora a evaluar</param>
        /// <param name="usingMac">Output: true si se usó MAC, false si se usó IP</param>
        /// <returns>Clave de agrupamiento o null si no tiene identificadores</returns>
        private string GetGroupingKey(PrinterEntity printer, out bool usingMac)
        {
            // Intentar usar MAC normalizada como clave (PREFERIDO)
            // RAZÓN: MAC es identificador físico inmutable, IP puede cambiar por DHCP
            if (!string.IsNullOrEmpty(printer.MacAddress))
            {
                // Normalizar MAC para garantizar formato consistente
                // RAZÓN: "AA:BB:CC" y "AA-BB-CC" deben generar misma clave
                string normalizedMac = MacAddressNormalizer.Normalize(printer.MacAddress);

                // Si normalización fue exitosa, usar como clave
                // RAZÓN: Garantiza que todas las variantes de formato se agrupen correctamente
                if (!string.IsNullOrEmpty(normalizedMac))
                {
                    usingMac = true; // Indicar que se usó MAC (identificador confiable)
                    return normalizedMac; // Retornar MAC normalizada como clave
                }
            }

            // Fallback: usar IP como clave si no tiene MAC
            // RAZÓN: Mejor agrupar por IP temporal que no agrupar nada
            // LIMITACIÓN: Si IP cambia por DHCP, se creará nuevo grupo hasta que se obtenga MAC
            if (!string.IsNullOrEmpty(printer.Ip))
            {
                usingMac = false; // Indicar que se usó IP (identificador temporal)
                return printer.Ip; // Retornar IP como clave
            }

            // No tiene ni MAC ni IP: impresora inválida
            // RAZÓN: No se puede monitorear ni agrupar sin identificadores
            usingMac = false;
            return null; // Retornar null indica que no se puede agrupar
        }
    }
}
