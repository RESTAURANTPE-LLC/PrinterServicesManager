using PrinterServices.Core.Network;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// INTERFAZ: Detección de degradación de latencia.
    /// PROPÓSITO: Abstracción para comparación con baseline (DIP).
    /// PRINCIPIO SRP: Solo define contrato de detección, no implementación.
    /// </summary>
    public interface IDegradationDetector
    {
        /// <summary>
        /// Detecta si latencia actual está degradada vs baseline.
        /// RAZÓN: Compara timing actual con baseline de impresora.
        /// </summary>
        /// <returns>
        /// null - Sin baseline aún (no se puede detectar)
        /// "healthy" - Latencia normal (<1.5x baseline)
        /// "degraded" - Latencia degradada (>2x baseline)
        /// "critical" - Latencia crítica (>2.5x baseline)
        /// </returns>
        string DetectDegradation(string impresoraId, LatencyTiming current);

        /// <summary>
        /// Marca impresora como degradada en BD.
        /// RAZÓN: UPDATE printer_latency_stats.is_degraded = 1.
        /// </summary>
        void MarkAsDegraded(string impresoraId, double factor);

        /// <summary>
        /// Marca impresora como recuperada en BD.
        /// RAZÓN: UPDATE printer_latency_stats.is_degraded = 0.
        /// </summary>
        void MarkAsRecovered(string impresoraId);
    }
}
