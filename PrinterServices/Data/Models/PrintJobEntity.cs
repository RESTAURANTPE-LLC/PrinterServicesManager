using PSQLite;

namespace PrinterServices.Data.Models
{
    [Table("print_jobs")]
    public class PrintJobEntity
    {
        [PrimaryKey]
        [Column("job_id")]
        public string JobId { get; set; }

        [Column("comanda_id")]
        public string ComandaId { get; set; }

        [NotNull]
        [Column("impresora_id")]
        public string ImpresoraId { get; set; }

        [NotNull]
        [Column("impresora_ip")]
        public string ImpresoraIp { get; set; }

        [Column("impresora_nombre")]
        public string ImpresoraNombre { get; set; }

        [Column("printer_model")]
        public string PrinterModel { get; set; }

        [Column("modo_impresion")]
        public string ModoImpresion { get; set; }

        [Column("contenido")]
        public string Contenido { get; set; }

        [Column("contenido_html")]
        public string ContenidoHtml { get; set; }

        [Column("device_id_origen")]
        public string DeviceIdOrigen { get; set; }

        [Column("ip_origen")]
        public string IpOrigen { get; set; }

        [Column("copias")]
        public int Copias { get; set; }

        [Column("prioridad")]
        public int Prioridad { get; set; }

        [Column("estado")]
        public string Estado { get; set; }

        [Column("reintentos")]
        public int Reintentos { get; set; }

        [Column("max_reintentos")]
        public int MaxReintentos { get; set; }

        [Column("error_mensaje")]
        public string ErrorMensaje { get; set; }

        [Column("fecha_creacion")]
        public string FechaCreacion { get; set; }

        [Column("fecha_impresion")]
        public string FechaImpresion { get; set; }

        [Column("tipo_impresion")]
        public string TipoImpresion { get; set; }

        [Column("lineas_imprimir_json")]
        public string LineasImprimirJson { get; set; }

        [Column("tamanio_letra")]
        public string TamanioLetra { get; set; }

        [Column("abre_gaveta")]
        public int AbreGaveta { get; set; }

        [Column("tipo_generacion")]
        public string TipoGeneracion { get; set; }

        [Column("qr_data")]
        public string QrData { get; set; }

        [Column("codigo_corte")]
        public string CodigoCorte { get; set; }

        public PrintJobEntity()
        {
            Copias = 1;
            Prioridad = 2;
            Estado = "PENDING";
            Reintentos = 0;
            MaxReintentos = 3;
        }
    }
}
