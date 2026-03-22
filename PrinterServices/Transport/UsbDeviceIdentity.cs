using System;

namespace PrinterServices.Transport
{
    /// <summary>
    /// Identidad inmutable de una impresora USB.
    /// Análogo a MacAddress para impresoras de red.
    /// No cambia aunque el usuario mueva la impresora a otro puerto USB.
    /// </summary>
    public class UsbDeviceIdentity
    {
        public string Vid { get; set; }           // Vendor ID (ej: "04B8" = Epson)
        public string Pid { get; set; }           // Product ID (ej: "0202")
        public string SerialNumber { get; set; }  // Único por unidad (ej: "J9SG012345")
        public string DevicePath { get; set; }    // Path actual (cambia con el puerto)
        public string FriendlyName { get; set; }  // Nombre legible (ej: "EPSON TM-T20II")

        /// <summary>
        /// Clave única para identificar el dispositivo físico.
        /// Equivalente a MacAddress normalizada en impresoras de red.
        /// Si tiene serial → VID+PID+Serial (identifica unidad exacta).
        /// Si no tiene serial → VID+PID (identifica modelo, ambiguo si hay 2 iguales).
        /// </summary>
        public string UniqueKey
        {
            get
            {
                if (!string.IsNullOrEmpty(SerialNumber))
                    return string.Format("{0}_{1}_{2}", Vid, Pid, SerialNumber).ToUpperInvariant();
                return string.Format("{0}_{1}", Vid, Pid).ToUpperInvariant();
            }
        }

        public override string ToString()
        {
            return string.Format("USB [{0}:{1} SN:{2}] → {3}",
                Vid, Pid, SerialNumber ?? "sin-serial", DevicePath ?? "desconectado");
        }
    }
}
