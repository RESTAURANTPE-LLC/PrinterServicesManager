using System;
using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Log de transiciones de estado de impresoras (ONLINE/OFFLINE).
    /// PROPÓSITO: Registrar cada vez que una impresora se desconecta o reconecta a la red,
    /// con timestamps exactos para generar reportes de disponibilidad.
    /// </summary>
    [Table("printer_status_log")]
    public class PrinterStatusLogEntity
    {
        [PrimaryKey, AutoIncrement]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>
        /// ID de la impresora (FK a printers.impresora_id).
        /// </summary>
        [Column("impresora_id")]
        [Indexed]
        public string ImpresoraId { get; set; }

        /// <summary>
        /// Nombre de la impresora al momento del evento.
        /// </summary>
        [Column("impresora_nombre")]
        public string ImpresoraNombre { get; set; }

        /// <summary>
        /// IP de la impresora al momento del evento.
        /// </summary>
        [Column("impresora_ip")]
        public string ImpresoraIp { get; set; }

        /// <summary>
        /// MAC de la impresora al momento del evento.
        /// </summary>
        [Column("mac_address")]
        public string MacAddress { get; set; }

        /// <summary>
        /// Estado anterior: ONLINE, OFFLINE, DISPONIBLE, NO_DISPONIBLE.
        /// </summary>
        [Column("estado_anterior")]
        public string EstadoAnterior { get; set; }

        /// <summary>
        /// Estado nuevo: ONLINE, OFFLINE, DISPONIBLE, NO_DISPONIBLE.
        /// </summary>
        [Column("estado_nuevo")]
        public string EstadoNuevo { get; set; }

        /// <summary>
        /// Detalle del cambio (ej: "sin papel", "tapa abierta", "conectividad restaurada").
        /// </summary>
        [Column("detalle")]
        public string Detalle { get; set; }

        /// <summary>
        /// Timestamp exacto del evento (ISO 8601).
        /// </summary>
        [Column("fecha")]
        [Indexed]
        public string Fecha { get; set; }
    }
}
