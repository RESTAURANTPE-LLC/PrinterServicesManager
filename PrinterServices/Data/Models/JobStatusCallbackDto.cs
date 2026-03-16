using System.Collections.Generic;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// DTO para notificar cambios de estado de jobs de impresión hacia QuipuNetX.
    /// Se envía al endpoint POST /api/rest/printerservice/updateJobStatus
    /// cuando PrintWorker cambia el estado de un job (DONE, FAILED, WAITING, etc.).
    /// Mismo patrón que IpChangeNotificationDto.
    /// </summary>
    public class JobStatusCallbackDto
    {
        /// <summary>
        /// ID del job en PrinterServices.
        /// </summary>
        public string job_id { get; set; } // Identificador del job

        /// <summary>
        /// Nuevo estado del job: PRINTING, DONE, FAILED, WAITING, CANCELLED, EXPIRED.
        /// </summary>
        public string status { get; set; } // Estado nuevo a notificar

        /// <summary>
        /// Lista de pedido_ids asociados al job.
        /// </summary>
        public List<string> pedido_ids { get; set; } // IDs de pedidos vinculados

        /// <summary>
        /// Mensaje de error (solo para FAILED/WAITING, vacío para DONE).
        /// </summary>
        public string error { get; set; } // Mensaje de error (opcional)

        /// <summary>
        /// Nombre de la impresora asignada al job.
        /// Usado por QuipuNetX para mostrar en la UI de Gestión de Impresiones.
        /// </summary>
        public string printer_name { get; set; } // Nombre de impresora (opcional)

        /// <summary>
        /// Área de producción / impresión (ej: "COCINA AUXILIAR", "BARRA").
        /// Usado por QuipuNetX para mostrar en la UI de Gestión de Impresiones.
        /// </summary>
        public string area_impresion { get; set; } // Área de producción (opcional)

        /// <summary>
        /// Constructor por defecto (requerido para deserialización JSON).
        /// </summary>
        public JobStatusCallbackDto()
        {
        }

        /// <summary>
        /// Constructor con parámetros para facilitar creación.
        /// </summary>
        /// <param name="jobId">ID del job</param>
        /// <param name="status">Nuevo estado</param>
        /// <param name="pedidoIds">Lista de pedido IDs</param>
        /// <param name="error">Mensaje de error (opcional)</param>
        public JobStatusCallbackDto(string jobId, string status, List<string> pedidoIds, string error = "", string printerName = "", string areaImpresion = "")
        {
            job_id = jobId;                 // Asignar ID del job
            this.status = status;           // Asignar nuevo estado
            pedido_ids = pedidoIds;         // Asignar lista de pedidos
            this.error = error;             // Asignar error (puede ser vacío)
            printer_name = printerName;     // Asignar nombre de impresora
            area_impresion = areaImpresion; // Asignar área de producción
        }
    }
}
