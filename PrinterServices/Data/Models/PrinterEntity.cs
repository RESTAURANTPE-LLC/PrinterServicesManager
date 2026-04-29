using PSQLite;

namespace PrinterServices.Data.Models
{
    [Table("printers")]
    public class PrinterEntity
    {
        [PrimaryKey]
        [Column("impresora_id")]
        public string ImpresoraId { get; set; }

        [Column("nombre")]
        public string Nombre { get; set; }

        [Column("ip")]
        public string Ip { get; set; }

        [Column("puerto")]
        public int Puerto { get; set; }

        [Column("mac_address")]
        public string MacAddress { get; set; }

        [Column("modelo")]
        public string Modelo { get; set; }

        [Column("modo_impresion")]
        public string ModoImpresion { get; set; }

        [Column("estado_online")]
        public int EstadoOnline { get; set; }

        [Column("tiene_papel")]
        public int TienePapel { get; set; }

        [Column("tapa_abierta")]
        public int TapaAbierta { get; set; }

        [Column("disponible_para_imprimir")]
        public int DisponibleParaImprimir { get; set; }

        [Column("ip_resuelta_por_arp")]
        public int IpResueltaPorArp { get; set; }

        [Column("snmp_enabled")]
        public int SnmpEnabled { get; set; }

        [Column("snmp_community")]
        public string SnmpCommunity { get; set; }

        [Column("ultimo_check")]
        public string UltimoCheck { get; set; }

        [Column("fecha_registro")]
        public string FechaRegistro { get; set; }

        // ─── Campos USB: Identificación de impresoras conectadas por USB ──────────────
        // RAZÓN: Análogo a MAC+IP para impresoras de red, pero para dispositivos USB.
        // UsbUniqueKey es inmutable (VID+PID+Serial), UsbDevicePath cambia si el usuario
        // mueve la impresora a otro puerto USB.

        [Column("tipo_conexion")]
        public string TipoConexion { get; set; }     // "RED" o "USB" (default "RED")

        [Column("usb_unique_key")]
        public string UsbUniqueKey { get; set; }     // "04B8_0202_J9SG012345" (inmutable, identifica unidad)

        [Column("usb_device_path")]
        public string UsbDevicePath { get; set; }    // Path actual (se actualiza si cambia de puerto USB)

        [Column("usb_friendly_name")]
        public string UsbFriendlyName { get; set; }  // Nombre legible (ej: "EPSON TM-T20II Receipt")

        // ─── Capabilities ESC/POS detectadas por PrinterCapabilityProbe ────────────────
        // RAZÓN: distinguir impresoras Epson genuinas (soportan GS(H fn48 para confirmar
        // procesamiento real de cada job) de clones que solo aceptan bytes. Sin este
        // perfil, PrintWorker no sabe qué método de confirmación usar y queda expuesto
        // a falsos positivos (DONE que no imprimió).

        [Column("capabilities_profile")]
        public string CapabilitiesProfile { get; set; }       // "unknown" | "epson_genuine" | "clone_compatible" | "clone_minimal" | "probe_failed"

        [Column("supports_dle_eot")]
        public int SupportsDleEot { get; set; }                // 1 si al menos un DLE EOT respondió

        [Column("supports_dle_eot_bits")]
        public string SupportsDleEotBits { get; set; }         // "P,O,E,S" (cuáles de los 4 andan)

        [Column("supports_asb")]
        public int SupportsAsb { get; set; }                   // 1 si GS a devolvió 4 bytes

        [Column("supports_process_id_response")]
        public int SupportsProcessIdResponse { get; set; }     // 1 si GS(H fn48 echoed el tag → confirmación real posible

        [Column("firmware_raw")]
        public string FirmwareRaw { get; set; }                // Hex de GS I 67 (ej: "45 50 53 4F 4E...")

        [Column("firmware_parsed")]
        public string FirmwareParsed { get; set; }             // Parseado humano (ej: "EPSON TM-T20IIIL 01.02")

        [Column("capabilities_detected_at")]
        public string CapabilitiesDetectedAt { get; set; }     // ISO 8601 con ms del último probe

        [Column("capabilities_probe_count")]
        public int CapabilitiesProbeCount { get; set; }        // Total de probes ejecutados sobre esta impresora

        [Column("capabilities_probe_duration_ms")]
        public int CapabilitiesProbeDurationMs { get; set; }   // Duración del último probe (para calibración)

        [Column("capabilities_last_error")]
        public string CapabilitiesLastError { get; set; }      // Último error del probe si hubo

        [Column("capabilities_last_trigger")]
        public string CapabilitiesLastTrigger { get; set; }    // "unknown" | "sync" | "periodic" | "manual" | "first_job_day"

        public PrinterEntity()
        {
            Puerto = 9100;
            EstadoOnline = 0;
            TienePapel = 1;
            TapaAbierta = 0;
            DisponibleParaImprimir = 0;
            IpResueltaPorArp = 0;
            SnmpEnabled = 0;
            SnmpCommunity = "public";
            TipoConexion = "RED";
            CapabilitiesProfile = "unknown";
            SupportsDleEot = 1;  // default optimista: asumimos que soporta DLE EOT hasta que el probe diga otra cosa
            SupportsAsb = 0;
            SupportsProcessIdResponse = 0;
            CapabilitiesProbeCount = 0;
        }
    }
}
