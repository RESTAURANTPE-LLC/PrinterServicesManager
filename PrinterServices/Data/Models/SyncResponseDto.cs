namespace PrinterServices.Data.Models
{
    /// <summary>
    /// DTO de respuesta para el endpoint POST /api/printers/sync.
    /// Informa a QuipuNetX el resultado de la sincronización.
    /// </summary>
    public class SyncResponseDto
    {
        /// <summary>
        /// Indica si la sincronización fue exitosa.
        /// </summary>
        public bool success { get; set; }

        /// <summary>
        /// Total de impresoras sincronizadas (inserted + updated).
        /// </summary>
        public int synchronized { get; set; }

        /// <summary>
        /// Número de impresoras nuevas insertadas en la BD.
        /// </summary>
        public int inserted { get; set; }

        /// <summary>
        /// Número de impresoras existentes actualizadas.
        /// </summary>
        public int updated { get; set; }

        /// <summary>
        /// Mensaje de error (solo si success = false).
        /// </summary>
        public string error { get; set; }

        /// <summary>
        /// Constructor por defecto.
        /// </summary>
        public SyncResponseDto()
        {
        }

        /// <summary>
        /// Constructor para respuesta exitosa.
        /// </summary>
        public static SyncResponseDto Success(int inserted, int updated)
        {
            return new SyncResponseDto
            {
                success = true,
                synchronized = inserted + updated,
                inserted = inserted,
                updated = updated,
                error = null
            };
        }

        /// <summary>
        /// Constructor para respuesta con error.
        /// </summary>
        public static SyncResponseDto Failure(string errorMessage)
        {
            return new SyncResponseDto
            {
                success = false,
                synchronized = 0,
                inserted = 0,
                updated = 0,
                error = errorMessage
            };
        }
    }
}
