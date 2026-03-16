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
        }
    }
}
