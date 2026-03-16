using PSQLite;
using System;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// Entidad que representa notificaciones de cambio de IP pendientes de enviar a QuipuNetX.
    /// Permite persistir notificaciones cuando QuipuNetX está offline y reintentarlas automáticamente.
    /// </summary>
    [Table("notificacionescambiosip")]
    public class IpChangeNotificationEntity
    {
        /// <summary>
        /// ID autoincremental de la notificación (PK).
        /// </summary>
        [PrimaryKey, AutoIncrement]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>
        /// MAC address de la impresora (invariable, usado como clave en QuipuNetX).
        /// Ejemplo: "AA:BB:CC:DD:EE:FF"
        /// </summary>
        [Column("mac_address")]
        [Indexed]
        public string MacAddress { get; set; }

        /// <summary>
        /// IP antigua detectada antes del cambio.
        /// Ejemplo: "192.168.68.193"
        /// </summary>
        [Column("old_ip")]
        public string OldIp { get; set; }

        /// <summary>
        /// IP nueva detectada por ARP scan.
        /// Ejemplo: "192.168.68.150"
        /// </summary>
        [Column("new_ip")]
        public string NewIp { get; set; }

        /// <summary>
        /// Estado de la notificación: PENDIENTE, ENVIADO, FALLIDO.
        /// </summary>
        [Column("estado")]
        [Indexed]
        public string Estado { get; set; }

        /// <summary>
        /// Fecha y hora de creación de la notificación.
        /// </summary>
        [Column("fecha_creacion")]
        public DateTime FechaCreacion { get; set; }

        /// <summary>
        /// Fecha y hora de envío exitoso a QuipuNetX.
        /// NULL si aún está pendiente o falló.
        /// </summary>
        [Column("fecha_envio")]
        public DateTime? FechaEnvio { get; set; }

        /// <summary>
        /// Número de intentos de envío realizados.
        /// </summary>
        [Column("intentos")]
        public int Intentos { get; set; }

        /// <summary>
        /// Último error capturado durante el envío (para debugging).
        /// </summary>
        [Column("ultimo_error")]
        public string UltimoError { get; set; }
    }
}
