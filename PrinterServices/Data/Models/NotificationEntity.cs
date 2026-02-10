using PSQLite;

namespace PrinterServices.Data.Models
{
    [Table("notifications")]
    public class NotificationEntity
    {
        [PrimaryKey, AutoIncrement]
        [Column("notif_id")]
        public int NotifId { get; set; }

        [NotNull]
        [Column("device_id")]
        public string DeviceId { get; set; }

        [NotNull]
        [Column("tipo")]
        public string Tipo { get; set; }

        [Column("comanda_id")]
        public string ComandaId { get; set; }

        [Column("job_id")]
        public string JobId { get; set; }

        [Column("impresora_id")]
        public string ImpresoraId { get; set; }

        [Column("impresora_nombre")]
        public string ImpresoraNombre { get; set; }

        [Column("mensaje")]
        public string Mensaje { get; set; }

        [Column("entregada")]
        public int Entregada { get; set; }

        [Column("fecha")]
        public string Fecha { get; set; }

        public NotificationEntity()
        {
            Entregada = 0;
        }
    }
}
