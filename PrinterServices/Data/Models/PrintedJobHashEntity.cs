using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// Tabla de hash de trabajos REALMENTE impresos físicamente.
    /// RAZÓN: Garantizar idempotencia — mismo job + mismo contenido = 1 sola impresión física.
    /// El hash se registra ANTES de enviar bytes a la impresora y NUNCA se borra.
    /// Usado por PrintWorker.ProcessJobAsync() para prevenir duplicación y por
    /// PrintJobManager.RecoverPending() para detectar jobs que ya se imprimieron antes de un crash.
    /// </summary>
    [Table("printed_jobs_hash")]
    public class PrintedJobHashEntity
    {
        [PrimaryKey]
        [Column("job_id")]
        public string JobId { get; set; }

        [NotNull]
        [Column("impresora_id")]
        public string ImpresoraId { get; set; }

        [NotNull]
        [Column("contenido_hash")]
        public string ContenidoHash { get; set; }  // SHA256 del contenido completo

        [NotNull]
        [Column("fecha_impresion")]
        public string FechaImpresion { get; set; }  // ISO 8601 cuando se registró

        [Column("impresora_nombre")]
        public string ImpresoraNombre { get; set; }

        [Column("area_impresion")]
        public string AreaImpresion { get; set; }

        [Column("tipo_impresion")]
        public string TipoImpresion { get; set; }

        [Column("pedido_ids")]
        public string PedidoIds { get; set; }  // Para auditoría: qué pedidos se imprimieron

        [Column("comanda_id")]
        public string ComandaId { get; set; }
    }
}
