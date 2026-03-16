using System;
using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Alerta de cambio/problema de red.
    /// PROPÓSITO: Log auditable de todos los eventos de red detectados.
    /// PRINCIPIO SRP: Solo representa alerta, sin lógica de notificación.
    /// </summary>
    [Table("network_alerts")]
    public class NetworkAlertEntity
    {
        /// <summary>
        /// ID autoincremental de la alerta (PK).
        /// </summary>
        [PrimaryKey, AutoIncrement]
        [Column("alert_id")]
        public int AlertId { get; set; }

        /// <summary>
        /// Tipo de alerta: 'printerservice_moved', 'latency_degraded', etc.
        /// RAZÓN: Para filtrar alertas por tipo en dashboard.
        /// </summary>
        [Column("alert_type")]
        public string AlertType { get; set; }

        /// <summary>
        /// Severidad: 'critical', 'warning', 'info'.
        /// RAZÓN: Priorizar alertas críticas en UI.
        /// </summary>
        [Column("severity")]
        public string Severity { get; set; }

        /// <summary>
        /// Gateway MAC anterior (antes del cambio).
        /// RAZÓN: Contexto para entender qué cambió.
        /// </summary>
        [Column("previous_gateway_mac")]
        public string PreviousGatewayMac { get; set; }

        /// <summary>
        /// Gateway MAC actual (después del cambio).
        /// </summary>
        [Column("current_gateway_mac")]
        public string CurrentGatewayMac { get; set; }

        /// <summary>
        /// Network ID anterior.
        /// </summary>
        [Column("previous_network_id")]
        public string PreviousNetworkId { get; set; }

        /// <summary>
        /// Network ID actual.
        /// </summary>
        [Column("current_network_id")]
        public string CurrentNetworkId { get; set; }

        /// <summary>
        /// Mensaje descriptivo de la alerta.
        /// RAZÓN: Para mostrar al usuario qué pasó.
        /// </summary>
        [Column("message")]
        public string Message { get; set; }

        /// <summary>
        /// Timestamp de detección de la alerta.
        /// </summary>
        [Column("detected_at")]
        [Indexed]
        public DateTime DetectedAt { get; set; }

        /// <summary>
        /// Flag de notificación enviada (0=pendiente, 1=enviada).
        /// RAZÓN: Para reintentar notificaciones pendientes a QuipuNetX.
        /// </summary>
        [Column("notified")]
        [Indexed]
        public int Notified { get; set; }
    }
}
