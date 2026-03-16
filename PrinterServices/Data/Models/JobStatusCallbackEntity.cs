using PSQLite;
using System;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// Entidad que representa callbacks de cambio de estado de jobs pendientes de enviar a QuipuNetX.
    /// Cuando PrinterServices cambia el estado de un job (DONE, FAILED, WAITING, etc.),
    /// notifica a QuipuNetX via HTTP POST. Si QuipuNetX está offline, persiste aquí para retry.
    /// Mismo patrón que IpChangeNotificationEntity.
    /// </summary>
    [Table("job_status_callbacks")]
    public class JobStatusCallbackEntity
    {
        /// <summary>
        /// ID autoincremental del callback (PK).
        /// </summary>
        [PrimaryKey, AutoIncrement]
        [Column("id")]
        public int Id { get; set; } // Clave primaria autoincremental

        /// <summary>
        /// ID del job de impresión en PrinterServices.
        /// Ejemplo: "714"
        /// </summary>
        [Column("job_id")]
        [Indexed]
        public string JobId { get; set; } // Identificador del job en PrinterServices

        /// <summary>
        /// Nuevo estado del job: PRINTING, DONE, FAILED, WAITING, CANCELLED, EXPIRED.
        /// </summary>
        [Column("status")]
        public string Status { get; set; } // Estado nuevo del job a notificar

        /// <summary>
        /// Lista de pedido_ids asociados al job, separados por coma.
        /// Ejemplo: "1772,1773"
        /// </summary>
        [Column("pedido_ids")]
        public string PedidoIds { get; set; } // IDs de pedidos separados por coma

        /// <summary>
        /// IP de origen del QuipuNetX que creó el job.
        /// Se usa para saber A DÓNDE enviar el callback HTTP.
        /// </summary>
        [Column("ip_origen")]
        public string IpOrigen { get; set; } // IP del QuipuNetX destino del callback

        /// <summary>
        /// Mensaje de error (solo para FAILED/WAITING).
        /// Puede ser vacío para DONE.
        /// </summary>
        [Column("error")]
        public string Error { get; set; } // Mensaje de error del job (opcional)

        /// <summary>
        /// Estado del envío del callback: PENDIENTE, ENVIADO, FALLIDO.
        /// </summary>
        [Column("estado_envio")]
        [Indexed]
        public string EstadoEnvio { get; set; } // Estado del callback (no del job)

        /// <summary>
        /// Fecha y hora de creación del callback.
        /// </summary>
        [Column("fecha_creacion")]
        public DateTime FechaCreacion { get; set; } // Timestamp de creación

        /// <summary>
        /// Fecha y hora de envío exitoso a QuipuNetX.
        /// NULL si aún está pendiente o falló.
        /// </summary>
        [Column("fecha_envio")]
        public DateTime? FechaEnvio { get; set; } // Timestamp de envío exitoso (null si pendiente)

        /// <summary>
        /// Número de intentos de envío realizados.
        /// </summary>
        [Column("intentos")]
        public int Intentos { get; set; } // Contador de reintentos

        /// <summary>
        /// Último error capturado durante el envío HTTP (para debugging).
        /// </summary>
        [Column("ultimo_error")]
        public string UltimoError { get; set; } // Último error HTTP para diagnóstico

        /// <summary>
        /// Fase 9: Indica si el destino es un cliente (puerto 8083) o servidor (puerto 8081).
        /// 1 = cliente (mini Nancy :8083, endpoint /api/ps/callback)
        /// 0 = servidor (WebServer_New :8081, endpoint /api/rest/printerservice/updateJobStatus)
        /// Se usa en RetryCallbackAsync para saber qué puerto y endpoint usar al reintentar.
        /// </summary>
        [Column("es_cliente")]
        public int EsCliente { get; set; } // 1 = cliente (:8083), 0 = servidor (:8081)
    }
}
