using PrinterServices.Core.Network;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// INTERFAZ: Verificación de salud de red.
    /// PROPÓSITO: Abstracción para comparación de configs (DIP).
    /// PRINCIPIO SRP: Solo define contrato de verificación, no implementación.
    /// </summary>
    public interface INetworkHealthChecker
    {
        /// <summary>
        /// Verifica si PrinterServices está en red correcta.
        /// RAZÓN: Compara config actual con última conocida buena.
        /// </summary>
        /// <returns>
        /// "healthy" - Misma red que snapshot bueno
        /// "changed" - Red diferente (gateway MAC distinto)
        /// "unknown" - No hay snapshot para comparar (primera ejecución)
        /// </returns>
        string CheckNetworkHealth(NetworkConfig currentConfig, NetworkConfig lastKnownGood);

        /// <summary>
        /// Actualiza estado actual de red en BD (singleton network_current).
        /// RAZÓN: Para que PrintWorker pueda consultar estado rápidamente.
        /// </summary>
        void UpdateCurrentNetworkStatus(NetworkConfig config, string status);

        /// <summary>
        /// Obtiene estado actual de red desde BD.
        /// RAZÓN: PrintWorker consulta esto antes de cada impresión.
        /// </summary>
        string GetCurrentNetworkStatus();
    }
}
