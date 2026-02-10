using PSQLite;

namespace PrinterServices.Data.Models
{
    [Table("print_log")]
    public class PrintLogEntity
    {
        [PrimaryKey, AutoIncrement]
        [Column("log_id")]
        public int LogId { get; set; }

        [Column("job_id")]
        public string JobId { get; set; }

        [Column("impresora_id")]
        public string ImpresoraId { get; set; }

        [Column("impresora_nombre")]
        public string ImpresoraNombre { get; set; }

        [Column("impresora_ip")]
        public string ImpresoraIp { get; set; }

        [Column("estado")]
        public string Estado { get; set; }

        [Column("mensaje")]
        public string Mensaje { get; set; }

        [Column("reintentos")]
        public int Reintentos { get; set; }

        [Column("fecha")]
        public string Fecha { get; set; }

        [Column("device_id_origen")]
        public string DeviceIdOrigen { get; set; }
    }
}
