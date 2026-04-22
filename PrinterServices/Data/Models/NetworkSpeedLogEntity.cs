using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Registro historico de mediciones de velocidad de red.
    /// TABLA: network_speed_log
    /// PROPOSITO: Almacenar velocidad de descarga y latencia cada 2 horas.
    /// </summary>
    [Table("network_speed_log")]
    public class NetworkSpeedLogEntity
    {
        [PrimaryKey, AutoIncrement]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>Velocidad de descarga en Kbps.</summary>
        [Column("download_speed_kbps")]
        public double DownloadSpeedKbps { get; set; }

        /// <summary>Latencia ICMP al gateway en milisegundos.</summary>
        [Column("latency_ms")]
        public int LatencyMs { get; set; }

        /// <summary>IP del gateway al momento de la medicion.</summary>
        [Column("gateway_ip")]
        public string GatewayIp { get; set; }

        /// <summary>Network ID (ej: 192.168.1.0/24) al momento de la medicion.</summary>
        [Column("network_id")]
        public string NetworkId { get; set; }

        /// <summary>Fecha/hora de la medicion en formato ISO 8601.</summary>
        [Column("measured_at")]
        public string MeasuredAt { get; set; }
    }
}
