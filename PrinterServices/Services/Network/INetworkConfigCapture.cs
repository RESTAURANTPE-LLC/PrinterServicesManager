using PrinterServices.Core.Network;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// INTERFAZ: Captura de configuración de red actual.
    /// PROPÓSITO: Abstracción para captura de red (DIP).
    /// PRINCIPIO SRP: Solo define contrato de captura, no implementación.
    /// </summary>
    public interface INetworkConfigCapture
    {
        /// <summary>
        /// Captura la configuración de red actual del sistema.
        /// RAZÓN: Obtiene gateway, IP propia, SSID WiFi, etc.
        /// </summary>
        /// <returns>NetworkConfig con datos capturados o null si falla.</returns>
        NetworkConfig CaptureCurrentConfig();
    }
}
