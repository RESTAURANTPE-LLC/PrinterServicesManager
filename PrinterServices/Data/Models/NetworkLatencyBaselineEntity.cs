using System;
using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Latencia base de red (sin carga de impresión).
    /// PROPÓSITO: Medir salud de red "pura" para comparar con latencia de impresión.
    /// PRINCIPIO SRP: Solo almacena mediciones de red, sin análisis.
    /// </summary>
    [Table("network_latency_baseline")]
    public class NetworkLatencyBaselineEntity
    {
        /// <summary>
        /// ID autoincremental del check (PK).
        /// </summary>
        [PrimaryKey, AutoIncrement]
        [Column("check_id")]
        public int CheckId { get; set; }

        /// <summary>
        /// IP del gateway medido.
        /// </summary>
        [Column("gateway_ip")]
        public string GatewayIp { get; set; }

        /// <summary>
        /// MAC del gateway medido.
        /// RAZÓN: Para agrupar mediciones por red física.
        /// </summary>
        [Column("gateway_mac")]
        [Indexed]
        public string GatewayMac { get; set; }

        /// <summary>
        /// Latencia de ping ICMP al gateway (milisegundos).
        /// RAZÓN: Indicador base de salud de red (normal <20ms).
        /// </summary>
        [Column("icmp_ping_ms")]
        public int? IcmpPingMs { get; set; }

        /// <summary>
        /// Latencia de resolución ARP (milisegundos).
        /// RAZÓN: Diagnóstico de problemas de tabla ARP.
        /// </summary>
        [Column("arp_latency_ms")]
        public int? ArpLatencyMs { get; set; }

        /// <summary>
        /// Network ID de la red medida.
        /// </summary>
        [Column("network_id")]
        public string NetworkId { get; set; }

        /// <summary>
        /// SSID WiFi si aplicable.
        /// </summary>
        [Column("wifi_ssid")]
        public string WifiSsid { get; set; }

        /// <summary>
        /// Nombre del adaptador usado.
        /// </summary>
        [Column("adapter_name")]
        public string AdapterName { get; set; }

        /// <summary>
        /// Timestamp del check.
        /// </summary>
        [Column("checked_at")]
        [Indexed]
        public DateTime CheckedAt { get; set; }
    }
}
