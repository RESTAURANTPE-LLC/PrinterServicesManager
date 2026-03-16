using System;
using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Snapshot de configuración de red conocida buena.
    /// PROPÓSITO: Registrar redes donde PrinterServices imprimió exitosamente.
    /// PRINCIPIO SRP: Solo representa datos de snapshot de red, sin lógica de negocio.
    /// </summary>
    [Table("network_snapshots")]
    public class NetworkSnapshotEntity
    {
        /// <summary>
        /// ID autoincremental del snapshot (PK).
        /// </summary>
        [PrimaryKey, AutoIncrement]
        [Column("snapshot_id")]
        public int SnapshotId { get; set; }

        /// <summary>
        /// MAC del gateway (identificador físico inmutable de red).
        /// RAZÓN: Mismo router = misma red, aunque cambie IP por DHCP.
        /// </summary>
        [Column("gateway_mac")]
        [Indexed]
        public string GatewayMac { get; set; }

        /// <summary>
        /// MAC del adaptador de PrinterServices (ethernet o WiFi activo).
        /// RAZÓN: Identificar si PrinterServices cambió de adaptador físico.
        /// </summary>
        [Column("adapter_mac")]
        public string AdapterMac { get; set; }

        /// <summary>
        /// IP del gateway cuando se capturó el snapshot.
        /// RAZÓN: Contexto, puede cambiar entre redes.
        /// </summary>
        [Column("gateway_ip")]
        public string GatewayIp { get; set; }

        /// <summary>
        /// IP de PrinterServices cuando imprimió exitosamente.
        /// RAZÓN: Trazabilidad de qué IP tenía en ese momento.
        /// </summary>
        [Column("printerservice_ip")]
        public string PrinterServiceIp { get; set; }

        /// <summary>
        /// Máscara de subred (ej: 255.255.255.0).
        /// RAZÓN: Para calcular network_id y validar pertenencia a subred.
        /// </summary>
        [Column("subnet_mask")]
        public string SubnetMask { get; set; }

        /// <summary>
        /// ID de red calculado (IP gateway &amp; subnet_mask, ej: 192.168.1.0/24).
        /// RAZÓN: Identificador lógico de subred, complementa gateway_mac.
        /// </summary>
        [Column("network_id")]
        [Indexed]
        public string NetworkId { get; set; }

        /// <summary>
        /// SSID del WiFi (NULL si es ethernet).
        /// RAZÓN: Diagnóstico para usuario ("Reconectar a WiFi RESTAURANT_WIFI").
        /// </summary>
        [Column("wifi_ssid")]
        public string WifiSsid { get; set; }

        /// <summary>
        /// Nombre del adaptador de red ("Ethernet", "Wi-Fi", etc.).
        /// RAZÓN: Diagnóstico para saber tipo de conexión.
        /// </summary>
        [Column("adapter_name")]
        public string AdapterName { get; set; }

        /// <summary>
        /// DNS primario usado en este snapshot.
        /// RAZÓN: Contexto adicional para diagnóstico avanzado.
        /// </summary>
        [Column("dns_primary")]
        public string DnsPrimary { get; set; }

        /// <summary>
        /// DNS secundario usado en este snapshot.
        /// </summary>
        [Column("dns_secondary")]
        public string DnsSecondary { get; set; }

        /// <summary>
        /// Timestamp de última impresión exitosa en esta red.
        /// RAZÓN: Para ordenar snapshots por más reciente.
        /// </summary>
        [Column("last_successful_print")]
        public DateTime LastSuccessfulPrint { get; set; }

        /// <summary>
        /// Contador de impresiones exitosas en esta red.
        /// RAZÓN: Métrica de confianza (más impresiones = más confiable).
        /// </summary>
        [Column("print_count")]
        public int PrintCount { get; set; }

        /// <summary>
        /// Flag de red confiable (1=confiable, 0=no confiable).
        /// RAZÓN: Permitir marcar redes como "no usar" si causan problemas.
        /// </summary>
        [Column("is_trusted")]
        public int IsTrusted { get; set; }
    }
}
