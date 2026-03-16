namespace PrinterServices.Data.Models
{
    /// <summary>
    /// DTO para notificar cambios de IP detectados por PrinterServices hacia QuipuNetX.
    /// Se envía al endpoint POST /api/rest/printers/update-ip cuando ArpScanWorker
    /// detecta que una impresora cambió de IP (DHCP).
    /// </summary>
    public class IpChangeNotificationDto
    {
        /// <summary>
        /// MAC address de la impresora (invariable, usado como clave).
        /// </summary>
        public string mac_address { get; set; }

        /// <summary>
        /// IP antigua (antes del cambio detectado por ARP).
        /// </summary>
        public string old_ip { get; set; }

        /// <summary>
        /// IP nueva detectada por ARP scan.
        /// </summary>
        public string new_ip { get; set; }

        /// <summary>
        /// Constructor por defecto (requerido para deserialización JSON).
        /// </summary>
        public IpChangeNotificationDto()
        {
        }

        /// <summary>
        /// Constructor con parámetros para facilitar creación.
        /// </summary>
        public IpChangeNotificationDto(string macAddress, string oldIp, string newIp)
        {
            mac_address = macAddress;
            old_ip = oldIp;
            new_ip = newIp;
        }
    }
}
