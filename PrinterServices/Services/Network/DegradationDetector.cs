using System;
using System.Linq;
using log4net;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// SERVICIO: Detección de degradación de latencia.
    /// PROPÓSITO: Comparar latencia actual con baseline y detectar anomalías.
    /// PRINCIPIO SRP: Solo responsable de detección, no de medición ni persistencia.
    /// </summary>
    public class DegradationDetector : IDegradationDetector
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(DegradationDetector));
        private readonly PrinterServiceDb _db;

        // RAZÓN: Umbrales configurables para alertas
        private const double WARNING_THRESHOLD = 2.0;   // 2x baseline = warning
        private const double CRITICAL_THRESHOLD = 2.5;  // 2.5x baseline = critical
        private const double HEALTHY_THRESHOLD = 1.5;   // <1.5x baseline = saludable

        public DegradationDetector(PrinterServiceDb db)
        {
            // RAZÓN: Inyección de dependencia (DIP)
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Detecta si latencia actual está degradada vs baseline.
        /// RAZÓN: Algoritmo de detección según vibe engineering.
        /// </summary>
        public string DetectDegradation(string impresoraId, LatencyTiming current)
        {
            if (current == null || string.IsNullOrEmpty(impresoraId))
            {
                return null;
            }

            // RAZÓN: Solo evaluar impresiones exitosas
            if (!current.Success || !current.TotalPrintMs.HasValue)
            {
                return null;
            }

            try
            {
                // RAZÓN: Obtener stats de impresora (incluye baseline)
                var stats = _db.Table<PrinterLatencyStatsEntity>()
                    .FirstOrDefault(s => s.ImpresoraId == impresoraId);

                if (stats == null || !stats.BaselineAvgMs.HasValue || stats.BaselineAvgMs.Value == 0)
                {
                    // RAZÓN: Sin baseline aún, no se puede detectar degradación
                    Log.Debug($"[DEGRADATION] {impresoraId} sin baseline (necesita 100 impresiones)");
                    return null;
                }

                // RAZÓN: Calcular factor de degradación (actual / baseline)
                double factor = (double)current.TotalPrintMs.Value / stats.BaselineAvgMs.Value;

                // RAZÓN: Evaluar según umbrales
                if (factor > CRITICAL_THRESHOLD)
                {
                    // ⚠⚠⚠ CRÍTICO: Latencia >2.5x baseline
                    Log.Error($"[DEGRADATION] ⚠⚠⚠ CRÍTICA en {impresoraId}:");
                    Log.Error($"  Baseline: {stats.BaselineAvgMs}ms");
                    Log.Error($"  Actual: {current.TotalPrintMs}ms ({factor:F2}x)");
                    Log.Error($"  Posibles causas:");
                    Log.Error($"    - Cable ethernet defectuoso");
                    Log.Error($"    - WiFi muy débil (mover servidor más cerca del router)");
                    Log.Error($"    - Router/switch saturado");
                    Log.Error($"    - Interferencia (microondas, otros WiFi)");

                    // RAZÓN: Marcar como degradada si no estaba marcada
                    if (stats.IsDegraded == 0)
                    {
                        MarkAsDegraded(impresoraId, factor);
                    }

                    return "critical";
                }
                else if (factor > WARNING_THRESHOLD)
                {
                    // ⚠ WARNING: Latencia >2x baseline
                    Log.Warn($"[DEGRADATION] ⚠ Degradación detectada en {impresoraId}:");
                    Log.Warn($"  Baseline: {stats.BaselineAvgMs}ms");
                    Log.Warn($"  Actual: {current.TotalPrintMs}ms ({factor:F2}x)");

                    // RAZÓN: Marcar como degradada si no estaba marcada
                    if (stats.IsDegraded == 0)
                    {
                        MarkAsDegraded(impresoraId, factor);
                    }

                    return "degraded";
                }
                else if (factor < HEALTHY_THRESHOLD && stats.IsDegraded == 1)
                {
                    // ✅ RECUPERACIÓN: Latencia volvió a normal
                    Log.Info($"[DEGRADATION] ✓ Recuperación de latencia en {impresoraId} ({factor:F2}x)");
                    MarkAsRecovered(impresoraId);
                    return "healthy";
                }
                else
                {
                    // Normal: latencia dentro de rango esperado
                    Log.Debug($"[DEGRADATION] {impresoraId} saludable ({factor:F2}x baseline)");
                    return "healthy";
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DEGRADATION] Error detectando degradación en {impresoraId}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Marca impresora como degradada en BD.
        /// RAZÓN: Persistir estado para consultas futuras.
        /// </summary>
        public void MarkAsDegraded(string impresoraId, double factor)
        {
            try
            {
                // RAZÓN: Actualizar flag de degradación
                var stats = _db.Table<PrinterLatencyStatsEntity>()
                    .FirstOrDefault(s => s.ImpresoraId == impresoraId);

                if (stats != null)
                {
                    stats.IsDegraded = 1;
                    stats.DegradationFactor = factor;
                    stats.DegradationSince = DateTime.Now;
                    _db.Update(stats);

                    Log.InfoFormat("[DEGRADATION] {0} marcada como DEGRADADA (factor: {1:F2}x)",
                        impresoraId, factor);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DEGRADATION] Error marcando degradación: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Marca impresora como recuperada en BD.
        /// RAZÓN: Limpiar flag de degradación.
        /// </summary>
        public void MarkAsRecovered(string impresoraId)
        {
            try
            {
                // RAZÓN: Limpiar flag de degradación
                var stats = _db.Table<PrinterLatencyStatsEntity>()
                    .FirstOrDefault(s => s.ImpresoraId == impresoraId);

                if (stats != null)
                {
                    stats.IsDegraded = 0;
                    stats.DegradationFactor = null;
                    stats.DegradationSince = null;
                    _db.Update(stats);

                    Log.InfoFormat("[DEGRADATION] {0} marcada como RECUPERADA", impresoraId);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DEGRADATION] Error marcando recuperación: {ex.Message}", ex);
            }
        }
    }
}
