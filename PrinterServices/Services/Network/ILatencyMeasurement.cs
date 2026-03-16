using PrinterServices.Core.Network;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// INTERFAZ: Medición y persistencia de latencias.
    /// PROPÓSITO: Abstracción para logging de timing (DIP).
    /// PRINCIPIO SRP: Solo define contrato de medición, no implementación.
    /// </summary>
    public interface ILatencyMeasurement
    {
        /// <summary>
        /// Persiste latencia de una impresión en BD.
        /// RAZÓN: INSERT en print_latency_log para historial completo.
        /// </summary>
        void RecordPrintLatency(LatencyTiming timing);

        /// <summary>
        /// Actualiza estadísticas agregadas de impresora.
        /// RAZÓN: UPDATE printer_latency_stats con promedios, percentiles, etc.
        /// </summary>
        void UpdatePrinterStats(string impresoraId);
    }
}
