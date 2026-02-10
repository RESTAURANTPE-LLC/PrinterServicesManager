using SQLite;

namespace PrinterServices.Data.Models
{
    [Table("network_config")]
    public class NetworkConfigEntity
    {
        [PrimaryKey]
        [Column("config_id")]
        public int ConfigId { get; set; }

        [NotNull]
        [Column("gateway_mac")]
        public string GatewayMac { get; set; }

        [Column("gateway_ip")]
        public string GatewayIp { get; set; }

        [Column("network_name")]
        public string NetworkName { get; set; }

        [Column("subnet")]
        public string Subnet { get; set; }

        [Column("auto_learned")]
        public int AutoLearned { get; set; }

        [Column("fecha_registro")]
        public string FechaRegistro { get; set; }

        [Column("activa")]
        public int Activa { get; set; }

        public NetworkConfigEntity()
        {
            AutoLearned = 1;
            Activa = 1;
        }
    }
}
