using System;
using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Log detallado de latencias de cada impresión.
    /// PROPÓSITO: Historial completo con timestamps para análisis de performance.
    /// PRINCIPIO SRP: Solo almacena datos de timing, sin cálculo de estadísticas.
    /// </summary>
    [Table("print_latency_log")]
    public class PrintLatencyLogEntity
    {
        /// <summary>
        /// ID autoincremental del log (PK).
        /// </summary>
        [PrimaryKey, AutoIncrement]
        [Column("log_id")]
        public int LogId { get; set; }

        /// <summary>
        /// ID del job de impresión asociado.
        /// RAZÓN: Correlación con print_jobs para trazabilidad completa.
        /// </summary>
        [Column("job_id")]
        public string JobId { get; set; }

        /// <summary>
        /// ID de la impresora.
        /// RAZÓN: Para agrupar latencias por impresora.
        /// </summary>
        [Column("impresora_id")]
        [Indexed]
        public string ImpresoraId { get; set; }

        /// <summary>
        /// IP de la impresora usada en esta impresión.
        /// RAZÓN: Diagnóstico de cambios de IP durante período.
        /// </summary>
        [Column("impresora_ip")]
        public string ImpresoraIp { get; set; }

        /// <summary>
        /// Timestamp cuando job fue encolado.
        /// RAZÓN: Para calcular queue_wait_ms = started_at - enqueued_at.
        /// </summary>
        [Column("enqueued_at")]
        public DateTime EnqueuedAt { get; set; }

        /// <summary>
        /// Timestamp cuando PrintWorker empezó a procesar el job.
        /// </summary>
        [Column("started_at")]
        public DateTime StartedAt { get; set; }

        /// <summary>
        /// Timestamp cuando TCP connect completó.
        /// RAZÓN: Para calcular tcp_connect_ms = tcp_connected_at - started_at.
        /// </summary>
        [Column("tcp_connected_at")]
        public DateTime? TcpConnectedAt { get; set; }

        /// <summary>
        /// Timestamp cuando datos fueron enviados completamente.
        /// RAZÓN: Para calcular data_send_ms = data_sent_at - tcp_connected_at.
        /// </summary>
        [Column("data_sent_at")]
        public DateTime? DataSentAt { get; set; }

        /// <summary>
        /// Timestamp cuando job completó (éxito o fallo).
        /// </summary>
        [Column("completed_at")]
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Latencia de espera en cola (milisegundos).
        /// RAZÓN: Diagnóstico de saturación de cola.
        /// </summary>
        [Column("queue_wait_ms")]
        public int? QueueWaitMs { get; set; }

        /// <summary>
        /// Latencia de TCP handshake (milisegundos).
        /// RAZÓN: Indicador de salud de red (normal <100ms).
        /// </summary>
        [Column("tcp_connect_ms")]
        public int? TcpConnectMs { get; set; }

        /// <summary>
        /// Latencia de envío de datos (milisegundos).
        /// RAZÓN: Indicador de throughput de red.
        /// </summary>
        [Column("data_send_ms")]
        public int? DataSendMs { get; set; }

        /// <summary>
        /// Latencia total end-to-end (milisegundos).
        /// RAZÓN: Métrica principal para detección de degradación.
        /// </summary>
        [Column("total_print_ms")]
        [Indexed]
        public int? TotalPrintMs { get; set; }

        /// <summary>
        /// Tamaño de los datos enviados (bytes).
        /// RAZÓN: Para calcular throughput (bytes/ms).
        /// </summary>
        [Column("data_size_bytes")]
        public int? DataSizeBytes { get; set; }

        /// <summary>
        /// Número de reintentos antes de completar.
        /// RAZÓN: Diagnóstico de estabilidad de conexión.
        /// </summary>
        [Column("retry_count")]
        public int RetryCount { get; set; }

        /// <summary>
        /// Flag de éxito (1=éxito, 0=fallo).
        /// RAZÓN: Para filtrar solo impresiones exitosas en baseline.
        /// </summary>
        [Column("success")]
        [Indexed]
        public int Success { get; set; }

        /// <summary>
        /// Mensaje de error si falló.
        /// </summary>
        [Column("error_message")]
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Gateway MAC de la red usada en esta impresión.
        /// RAZÓN: Correlación latencia con red específica.
        /// </summary>
        [Column("gateway_mac")]
        public string GatewayMac { get; set; }

        /// <summary>
        /// Network ID de la red usada.
        /// </summary>
        [Column("network_id")]
        public string NetworkId { get; set; }

        /// <summary>
        /// SSID WiFi si aplicable.
        /// </summary>
        [Column("wifi_ssid")]
        public string WifiSsid { get; set; }

        /// <summary>
        /// Timestamp de creación del registro.
        /// </summary>
        [Column("created_at")]
        [Indexed]
        public DateTime CreatedAt { get; set; }
    }
}
