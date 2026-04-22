using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Dispositivo descubierto en la red via ARP scan.
    /// TABLA: devices_on_network
    /// PROPOSITO: Registro de todos los dispositivos encontrados en la red local.
    /// </summary>
    [Table("devices_on_network")]
    public class DeviceOnNetworkEntity
    {
        [PrimaryKey, AutoIncrement]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>Direccion IP del dispositivo.</summary>
        [Column("ip_address")]
        public string IpAddress { get; set; }

        /// <summary>Direccion MAC del dispositivo (identificador unico).</summary>
        [Column("mac_address")]
        public string MacAddress { get; set; }

        /// <summary>Nombre DNS del dispositivo (si se pudo resolver).</summary>
        [Column("hostname")]
        public string Hostname { get; set; }

        /// <summary>Fabricante basado en OUI (primeros 3 bytes MAC).</summary>
        [Column("vendor")]
        public string Vendor { get; set; }

        /// <summary>Tipo inferido: "printer", "router", "gateway", "unknown".</summary>
        [Column("device_type")]
        public string DeviceType { get; set; }

        /// <summary>Primera vez que se descubrio este dispositivo.</summary>
        [Column("first_seen_at")]
        public string FirstSeenAt { get; set; }

        /// <summary>Ultima vez que se detecto online.</summary>
        [Column("last_seen_at")]
        public string LastSeenAt { get; set; }

        /// <summary>1=online, 0=offline (no respondio en el ultimo scan).</summary>
        [Column("is_online")]
        public int IsOnline { get; set; }

        /// <summary>Network ID al momento del descubrimiento.</summary>
        [Column("network_id")]
        public string NetworkId { get; set; }

        /// <summary>MAC del gateway bajo el cual se descubrio.</summary>
        [Column("gateway_mac")]
        public string GatewayMac { get; set; }
    }
}
