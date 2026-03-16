using System;
using System.Linq;
using log4net;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// SERVICIO: Medición y persistencia de latencias.
    /// PROPÓSITO: Guardar timing de impresiones y calcular estadísticas.
    /// PRINCIPIO SRP: Solo responsable de persistir latencias, no de medirlas.
    /// </summary>
    public class LatencyMeasurement : ILatencyMeasurement
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(LatencyMeasurement));
        private readonly PrinterServiceDb _db;

        public LatencyMeasurement(PrinterServiceDb db)
        {
            // RAZÓN: Inyección de dependencia (DIP)
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Persiste latencia de una impresión en BD.
        /// RAZÓN: Historial completo para análisis posterior.
        /// </summary>
        public void RecordPrintLatency(LatencyTiming timing)
        {
            if (timing == null)
            {
                Log.Warn("[LATENCY] Timing null, no se puede registrar");
                return;
            }

            try
            {
                // RAZÓN: Convertir LatencyTiming a entity para BD
                var entity = new PrintLatencyLogEntity
                {
                    JobId = timing.JobId,
                    ImpresoraId = timing.ImpresoraId,
                    ImpresoraIp = timing.ImpresoraIp,
                    EnqueuedAt = timing.EnqueuedAt,
                    StartedAt = timing.StartedAt,
                    TcpConnectedAt = timing.TcpConnectedAt,
                    DataSentAt = timing.DataSentAt,
                    CompletedAt = timing.CompletedAt,
                    QueueWaitMs = timing.QueueWaitMs,
                    TcpConnectMs = timing.TcpConnectMs,
                    DataSendMs = timing.DataSendMs,
                    TotalPrintMs = timing.TotalPrintMs,
                    DataSizeBytes = timing.DataSizeBytes,
                    RetryCount = timing.RetryCount,
                    Success = timing.Success ? 1 : 0,
                    ErrorMessage = timing.ErrorMessage,
                    GatewayMac = timing.GatewayMac,
                    NetworkId = timing.NetworkId,
                    WifiSsid = timing.WifiSsid,
                    CreatedAt = DateTime.Now
                };

                _db.Insert(entity);

                Log.DebugFormat("[LATENCY] Registrado: Job {0}, Total {1}ms (TCP:{2}ms, Send:{3}ms)",
                    timing.JobId, timing.TotalPrintMs, timing.TcpConnectMs, timing.DataSendMs);
            }
            catch (Exception ex)
            {
                Log.Error("[LATENCY] Error registrando latencia: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Actualiza estadísticas agregadas de impresora.
        /// RAZÓN: Pre-cálculo para alertas rápidas sin query pesado.
        /// </summary>
        public void UpdatePrinterStats(string impresoraId)
        {
            if (string.IsNullOrEmpty(impresoraId)) return;

            try
            {
                // RAZÓN: Obtener o crear stats para esta impresora
                var stats = _db.Table<PrinterLatencyStatsEntity>()
                    .FirstOrDefault(s => s.ImpresoraId == impresoraId);

                if (stats == null)
                {
                    stats = new PrinterLatencyStatsEntity
                    {
                        ImpresoraId = impresoraId,
                        IsDegraded = 0
                    };
                }

                // RAZÓN: Calcular estadísticas últimas 24h (solo éxitos)
                // RAZÓN: Pre-calcular fecha límite fuera del lambda para que PSQLite la pase como parámetro SQL literal,
                // ya que SQLite no tiene función nativa 'addhours'
                var desde24h = DateTime.Now.AddHours(-24);
                var last24h = _db.Table<PrintLatencyLogEntity>()
                    .Where(l => l.ImpresoraId == impresoraId)
                    .Where(l => l.Success == 1)
                    .Where(l => l.CreatedAt >= desde24h)
                    .OrderBy(l => l.TotalPrintMs)
                    .ToList();

                if (last24h.Any())
                {
                    stats.Last24hPrints = last24h.Count;
                    stats.Last24hAvgMs = (int)last24h.Average(l => l.TotalPrintMs ?? 0);
                    stats.Last24hMaxMs = last24h.Max(l => l.TotalPrintMs ?? 0);

                    // RAZÓN: Calcular percentil 95 (índice = 95% del total)
                    int p95Index = (int)Math.Ceiling(0.95 * last24h.Count) - 1;
                    if (p95Index >= 0 && p95Index < last24h.Count)
                    {
                        stats.Last24hP95Ms = last24h[p95Index].TotalPrintMs;
                    }
                }

                // RAZÓN: Establecer baseline si aún no existe (primeras 100 impresiones exitosas)
                if (stats.BaselineAvgMs == null || stats.BaselineAvgMs == 0)
                {
                    var first100 = _db.Table<PrintLatencyLogEntity>()
                        .Where(l => l.ImpresoraId == impresoraId)
                        .Where(l => l.Success == 1)
                        .OrderBy(l => l.CreatedAt)
                        .Take(100)
                        .ToList();

                    if (first100.Count >= 100)
                    {
                        stats.BaselineAvgMs = (int)first100.Average(l => l.TotalPrintMs ?? 0);

                        // RAZÓN: P95 del baseline
                        var sorted = first100.OrderBy(l => l.TotalPrintMs).ToList();
                        int p95Idx = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
                        if (p95Idx >= 0 && p95Idx < sorted.Count)
                        {
                            stats.BaselineP95Ms = sorted[p95Idx].TotalPrintMs;
                        }

                        stats.BaselineEstablishedAt = DateTime.Now;

                        Log.InfoFormat("[LATENCY] Baseline establecido para {0}: {1}ms avg (P95: {2}ms)",
                            impresoraId, stats.BaselineAvgMs, stats.BaselineP95Ms);
                    }
                }

                stats.LastUpdated = DateTime.Now;

                // RAZÓN: InsertOrReplace maneja ambos casos
                _db.InsertOrReplace(stats);

                Log.DebugFormat("[LATENCY] Stats actualizadas para {0}: Avg 24h={1}ms, Baseline={2}ms",
                    impresoraId, stats.Last24hAvgMs, stats.BaselineAvgMs);
            }
            catch (Exception ex)
            {
                Log.Error($"[LATENCY] Error actualizando stats de {impresoraId}: {ex.Message}", ex);
            }
        }
    }
}
