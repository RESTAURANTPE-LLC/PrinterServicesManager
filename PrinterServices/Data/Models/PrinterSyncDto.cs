namespace PrinterServices.Data.Models
{
    /// <summary>
    /// DTO para sincronización de impresoras desde QuipuNetX hacia PrinterServices.
    /// QuipuNetX envía un array de estos objetos al endpoint POST /api/printers/sync
    /// al iniciar el servidor.
    /// </summary>
    public class PrinterSyncDto
    {
        /// <summary>
        /// ID único de la impresora (PK en ambas BDs).
        /// </summary>
        public string impresora_id { get; set; }

        /// <summary>
        /// Nombre descriptivo de la impresora.
        /// </summary>
        public string nombre { get; set; }

        /// <summary>
        /// IP actual de la impresora en formato IPv4.
        /// </summary>
        public string ip { get; set; }

        /// <summary>
        /// Puerto de impresión TCP (generalmente 9100 para ESC/POS).
        /// </summary>
        public int puerto { get; set; }

        /// <summary>
        /// MAC address de la impresora física (invariable).
        /// </summary>
        public string mac_address { get; set; }

        /// <summary>
        /// Estado de la impresora en QuipuNetX.
        /// </summary>
        public string estado { get; set; }

        /// <summary>
        /// Constructor por defecto (requerido para deserialización JSON).
        /// </summary>
        public PrinterSyncDto()
        {
        }
    }
}
