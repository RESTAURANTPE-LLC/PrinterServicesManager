using System;
using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Estado actual de red de PrinterServices (SINGLETON - solo 1 fila).
    /// PROPÓSITO: Almacenar configuración de red actual para consulta rápida por API.
    /// PRINCIPIO SRP: Solo representa estado actual, sin lógica de comparación.
    /// </summary>
    [Table("network_current")]
    public class NetworkCurrentEntity
    {
        /// <summary>
        /// ID fijo = 1 (solo existe 1 fila en esta tabla).
        /// RAZÓN: Singleton pattern en BD para acceso directo sin query.
        /// </summary>
        [PrimaryKey]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>
        /// MAC del gateway actual.
        /// </summary>
        [Column("gateway_mac")]
        public string GatewayMac { get; set; }

        /// <summary>
        /// IP del gateway actual.
        /// </summary>
        [Column("gateway_ip")]
        public string GatewayIp { get; set; }

        /// <summary>
        /// IP actual de PrinterServices.
        /// </summary>
        [Column("printerservice_ip")]
        public string PrinterServiceIp { get; set; }

        /// <summary>
        /// Máscara de subred actual.
        /// </summary>
        [Column("subnet_mask")]
        public string SubnetMask { get; set; }

        /// <summary>
        /// Network ID actual calculado.
        /// </summary>
        [Column("network_id")]
        public string NetworkId { get; set; }

        /// <summary>
        /// SSID WiFi actual (NULL si ethernet).
        /// </summary>
        [Column("wifi_ssid")]
        public string WifiSsid { get; set; }

        /// <summary>
        /// Nombre del adaptador activo.
        /// </summary>
        [Column("adapter_name")]
        public string AdapterName { get; set; }

        /// <summary>
        /// MAC del adaptador activo.
        /// </summary>
        [Column("adapter_mac")]
        public string AdapterMac { get; set; }

        /// <summary>
        /// Estado de salud de red: 'unknown', 'healthy', 'changed', 'degraded'.
        /// RAZÓN: PrintWorker consulta este campo para decidir si procesar jobs.
        /// </summary>
        [Column("status")]
        public string Status { get; set; }

        /// <summary>
        /// FK a network_snapshots: ID del snapshot que coincide con red actual.
        /// RAZÓN: NULL si no coincide con ninguna red conocida buena.
        /// </summary>
        [Column("matched_snapshot_id")]
        public int? MatchedSnapshotId { get; set; }

        /// <summary>
        /// Timestamp del último check de NetworkWatcher.
        /// RAZÓN: Para saber qué tan actualizado está este estado.
        /// </summary>
        [Column("last_check")]
        public DateTime? LastCheck { get; set; }
    }
}
