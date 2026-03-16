using System;
using System.Collections.Generic;
using System.Net;
using log4net;
using SnmpSharpNet;

namespace PrinterServices.Core.Network
{
    /// <summary>
    /// Helper para monitoreo de impresoras vía SNMP (Simple Network Management Protocol).
    /// VENTAJA: 80% menos overhead que DLE EOT (1 paquete UDP vs 3-4 TCP).
    /// Usado por StatusMonitor para polling ligero, complementa DLE EOT en PrintWorker.
    /// </summary>
    public static class SnmpHelper
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(SnmpHelper));

        // ═══════════════════════════════════════════════════════════
        // OIDs estándar RFC 3805 (Printer MIB) - Universal
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// OID: Estado del papel en bandeja principal.
        /// Valores: 0=Otro, 3=Vacío, 4=Bajo, 5=Lleno
        /// </summary>
        public const string OID_PAPER_STATUS = "1.3.6.1.2.1.43.8.2.1.10.1.1";

        /// <summary>
        /// OID: Nivel actual de papel (porcentaje).
        /// Valores: 0-100 (-2=desconocido, -3=no aplica)
        /// </summary>
        public const string OID_PAPER_LEVEL = "1.3.6.1.2.1.43.11.1.1.6.1.1";

        /// <summary>
        /// OID: Estado de puerta/tapa de la impresora.
        /// Valores: 3=Cerrada, 4=Abierta, 5=InterlockAbierta
        /// </summary>
        public const string OID_COVER_STATUS = "1.3.6.1.2.1.43.6.1.1.8.1.1";

        /// <summary>
        /// OID: Estado general del dispositivo (Host Resources MIB).
        /// Valores: 1=Desconocido, 2=Running, 3=Warning, 4=Testing, 5=Down
        /// </summary>
        public const string OID_DEVICE_STATUS = "1.3.6.1.2.1.25.3.2.1.5.1";

        /// <summary>
        /// OID: Estado detallado de la impresora (Printer MIB).
        /// Bitmap: bit0=Otro, bit1=Desconocido, bit2=Idle, bit3=Imprimiendo, bit4=Warmup
        /// </summary>
        public const string OID_PRINTER_STATUS = "1.3.6.1.2.1.43.5.1.1.1.1";

        /// <summary>
        /// OID: Descripción del dispositivo.
        /// </summary>
        public const string OID_DEVICE_DESCRIPTION = "1.3.6.1.2.1.25.3.2.1.3.1";

        /// <summary>
        /// OID: Contador total de páginas impresas.
        /// </summary>
        public const string OID_PAGE_COUNTER = "1.3.6.1.2.1.43.10.2.1.4.1.1";

        /// <summary>
        /// OID: Estado de errores (bitmap).
        /// bit0=LowPaper, bit1=NoPaper, bit2=LowToner, bit3=NoToner, 
        /// bit4=DoorOpen, bit5=Jammed, bit6=Offline, bit7=ServiceRequested
        /// </summary>
        public const string OID_PRINTER_ERRORS = "1.3.6.1.2.1.43.5.1.1.5.1";

        // ═══════════════════════════════════════════════════════════
        // OIDs específicos para impresoras térmicas ESC/POS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// OID: Temperatura del cabezal térmico (enterprise-specific).
        /// Nota: Puede variar según fabricante (Epson, Star, Bixolon).
        /// </summary>
        public const string OID_THERMAL_HEAD_TEMP = "1.3.6.1.4.1.1248.1.2.2.1.1.1.4.1.1";

        /// <summary>
        /// OID: Tipo de papel (Normal, Recibo, Etiqueta).
        /// Enterprise-specific, común en Epson TM series.
        /// </summary>
        public const string OID_PAPER_TYPE = "1.3.6.1.4.1.1248.1.2.2.44.1.1.2.1.4.1.1";

        // ═══════════════════════════════════════════════════════════
        // Modelo de datos para respuesta SNMP
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Estado de impresora obtenido vía SNMP.
        /// Mapea OIDs a propiedades legibles.
        /// </summary>
        public class SnmpPrinterStatus
        {
            // Estado de conectividad (si SNMP respondió, impresora está online)
            public bool Online { get; set; }

            // Estado de papel
            public bool TienePapel { get; set; }
            public int PaperLevel { get; set; } // 0-100 porcentaje

            // Estado de tapa/puerta
            public bool TapaAbierta { get; set; }

            // Estado general
            public bool DisponibleParaImprimir { get; set; }

            // Info adicional
            public string DeviceDescription { get; set; }
            public long PageCounter { get; set; }
            public int DeviceStatus { get; set; } // 1-5 (ver OID_DEVICE_STATUS)

            // Raw values para diagnóstico
            public Dictionary<string, string> RawOids { get; set; }
        }

        // ═══════════════════════════════════════════════════════════
        // Métodos públicos para verificación SNMP
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Verifica estado de impresora vía SNMP (protocolo estándar, 80% menos overhead que TCP).
        /// Usado por StatusMonitor para polling ligero cada 5-15s.
        /// </summary>
        /// <param name="ipAddress">IP de la impresora.</param>
        /// <param name="community">Community string SNMP (default: "public").</param>
        /// <param name="timeoutMs">Timeout en milisegundos (default: 2000ms).</param>
        /// <returns>Estado de impresora o null si no responde/SNMP no habilitado.</returns>
        public static SnmpPrinterStatus CheckPrinter(string ipAddress, string community = "public", int timeoutMs = 2000)
        {
            if (string.IsNullOrEmpty(ipAddress)) // Validar entrada
            {
                return null;
            }

            try
            {
                // Configurar agente SNMP (UDP, puerto 161)
                var agent = new IpAddress(ipAddress);
                var target = new UdpTarget((IPAddress)agent, 161, timeoutMs, 1); // 1 retry

                // Crear PDU (Protocol Data Unit) con OIDs a consultar
                var oids = new List<Oid>
                {
                    new Oid(OID_PAPER_LEVEL),       // Nivel papel
                    new Oid(OID_COVER_STATUS),      // Estado tapa
                    new Oid(OID_DEVICE_STATUS),     // Estado general
                    new Oid(OID_PRINTER_STATUS),    // Estado impresora
                    new Oid(OID_DEVICE_DESCRIPTION),// Descripción
                    new Oid(OID_PAGE_COUNTER)       // Contador páginas
                };

                var pdu = new Pdu(PduType.Get);
                foreach (var oid in oids)
                {
                    pdu.VbList.Add(oid); // Agregar OID a la consulta
                }

                // Crear parámetros SNMP v2c (más común)
                var param = new AgentParameters(SnmpVersion.Ver2, new OctetString(community));

                // Enviar request SNMP (1 paquete UDP)
                var result = (SnmpV2Packet)target.Request(pdu, param);

                target.Close(); // Cerrar socket

                if (result == null || result.Pdu.ErrorStatus != 0) // Error en respuesta SNMP
                {
                    Log.DebugFormat("[SNMP] Error en respuesta de {0}: código {1}",
                        ipAddress, result?.Pdu.ErrorStatus ?? -1);
                    return null;
                }

                // Parsear respuesta SNMP a modelo de datos
                return ParseSnmpResponse(result, ipAddress);
            }
            catch (Exception ex)
            {
                // SNMP no habilitado, timeout, o impresora offline
                Log.DebugFormat("[SNMP] No se pudo consultar {0}: {1}", ipAddress, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Parsea respuesta SNMP a modelo de datos legible.
        /// </summary>
        private static SnmpPrinterStatus ParseSnmpResponse(SnmpV2Packet packet, string ipAddress)
        {
            var status = new SnmpPrinterStatus
            {
                Online = true, // Si llegamos aquí, SNMP respondió → impresora online
                RawOids = new Dictionary<string, string>()
            };

            try
            {
                // Recorrer valores recibidos (VarBind)
                foreach (Vb vb in packet.Pdu.VbList)
                {
                    string oidStr = vb.Oid.ToString();
                    string value = vb.Value.ToString();

                    status.RawOids[oidStr] = value; // Guardar raw para diagnóstico

                    // Parsear cada OID según su tipo
                    if (oidStr.StartsWith(OID_PAPER_LEVEL))
                    {
                        // Nivel de papel: 0-100 (negativo = desconocido)
                        int level = ParseInt(value, -1);
                        status.TienePapel = level > 10; // Considerar "sin papel" si <10%
                        status.PaperLevel = level >= 0 ? level : 0;
                    }
                    else if (oidStr.StartsWith(OID_COVER_STATUS))
                    {
                        // Estado tapa: 3=Cerrada, 4=Abierta
                        int coverStatus = ParseInt(value, 3);
                        status.TapaAbierta = (coverStatus == 4 || coverStatus == 5);
                    }
                    else if (oidStr.StartsWith(OID_DEVICE_STATUS))
                    {
                        // Estado dispositivo: 2=Running, 3=Warning, 5=Down
                        status.DeviceStatus = ParseInt(value, 1);
                    }
                    else if (oidStr.StartsWith(OID_DEVICE_DESCRIPTION))
                    {
                        // Descripción del dispositivo
                        status.DeviceDescription = value;
                    }
                    else if (oidStr.StartsWith(OID_PAGE_COUNTER))
                    {
                        // Contador de páginas
                        status.PageCounter = ParseLong(value, 0);
                    }
                }

                // Calcular DisponibleParaImprimir (lógica combinada)
                status.DisponibleParaImprimir =
                    status.Online &&
                    status.TienePapel &&
                    !status.TapaAbierta &&
                    status.DeviceStatus == 2; // 2 = Running

                Log.DebugFormat("[SNMP] {0} → Online={1} Papel={2}% Tapa={3} Disponible={4}",
                    ipAddress, status.Online, status.PaperLevel,
                    status.TapaAbierta ? "Abierta" : "Cerrada",
                    status.DisponibleParaImprimir);
            }
            catch (Exception ex)
            {
                Log.Warn("[SNMP] Error parseando respuesta de " + ipAddress + ": " + ex.Message);
            }

            return status;
        }

        // ═══════════════════════════════════════════════════════════
        // Helpers para parsing de valores SNMP
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Parsea valor SNMP a entero, retorna defaultValue si falla.
        /// </summary>
        private static int ParseInt(string value, int defaultValue)
        {
            if (int.TryParse(value, out int result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// Parsea valor SNMP a long, retorna defaultValue si falla.
        /// </summary>
        private static long ParseLong(string value, long defaultValue)
        {
            if (long.TryParse(value, out long result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// Verifica si una impresora tiene SNMP habilitado (test rápido).
        /// </summary>
        public static bool IsSnmpEnabled(string ipAddress, string community = "public")
        {
            try
            {
                var agent = new IpAddress(ipAddress);
                var target = new UdpTarget((IPAddress)agent, 161, 1000, 1); // Timeout corto: 1s

                var pdu = new Pdu(PduType.Get);
                pdu.VbList.Add(new Oid(OID_DEVICE_STATUS)); // OID básico

                var param = new AgentParameters(SnmpVersion.Ver2, new OctetString(community));
                var result = target.Request(pdu, param);

                target.Close();

                return result != null; // Si respondió, SNMP habilitado
            }
            catch
            {
                return false; // SNMP no habilitado o impresora offline
            }
        }
    }
}
