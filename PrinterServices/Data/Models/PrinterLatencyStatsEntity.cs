using System;
using PSQLite;

namespace PrinterServices.Data.Models
{
    /// <summary>
    /// ENTIDAD: Estadísticas agregadas de latencia por impresora.
    /// PROPÓSITO: Pre-cálculo para alertas rápidas sin query pesado.
    /// PRINCIPIO SRP: Solo almacena estadísticas, el cálculo es externo.
    /// </summary>
    [Table("printer_latency_stats")]
    public class PrinterLatencyStatsEntity
    {
        /// <summary>
        /// ID de la impresora (PK).
        /// </summary>
        [PrimaryKey]
        [Column("impresora_id")]
        public string ImpresoraId { get; set; }

        /// <summary>
        /// Número de impresiones exitosas en últimas 24h.
        /// RAZÓN: Métrica de actividad reciente.
        /// </summary>
        [Column("last_24h_prints")]
        public int Last24hPrints { get; set; }

        /// <summary>
        /// Latencia promedio en últimas 24h (milisegundos).
        /// RAZÓN: Comparar con baseline para detectar degradación progresiva.
        /// </summary>
        [Column("last_24h_avg_ms")]
        public int? Last24hAvgMs { get; set; }

        /// <summary>
        /// Percentil 95 en últimas 24h (milisegundos).
        /// RAZÓN: Métrica más robusta que promedio (ignora outliers).
        /// </summary>
        [Column("last_24h_p95_ms")]
        public int? Last24hP95Ms { get; set; }

        /// <summary>
        /// Latencia máxima en últimas 24h (milisegundos).
        /// RAZÓN: Detectar spikes anormales.
        /// </summary>
        [Column("last_24h_max_ms")]
        public int? Last24hMaxMs { get; set; }

        /// <summary>
        /// Latencia promedio baseline (primeras 100 impresiones exitosas).
        /// RAZÓN: Representa "latencia normal" de esta impresora en condiciones óptimas.
        /// IMPORTANTE: NO se recalcula automáticamente para evitar normalización de degradación.
        /// </summary>
        [Column("baseline_avg_ms")]
        public int? BaselineAvgMs { get; set; }

        /// <summary>
        /// Percentil 95 baseline.
        /// </summary>
        [Column("baseline_p95_ms")]
        public int? BaselineP95Ms { get; set; }

        /// <summary>
        /// Timestamp cuando se estableció el baseline.
        /// RAZÓN: Trazabilidad de cuándo se capturó el "estado normal".
        /// </summary>
        [Column("baseline_established_at")]
        public DateTime? BaselineEstablishedAt { get; set; }

        /// <summary>
        /// Flag de degradación detectada (1=degradada, 0=normal).
        /// RAZÓN: Para consulta rápida sin recalcular factor.
        /// </summary>
        [Column("is_degraded")]
        public int IsDegraded { get; set; }

        /// <summary>
        /// Factor de degradación actual (ej: 2.5 = latencia 2.5x mayor que baseline).
        /// RAZÓN: Métrica principal para alertas (>2.0 = warning, >2.5 = critical).
        /// </summary>
        [Column("degradation_factor")]
        public double? DegradationFactor { get; set; }

        /// <summary>
        /// Timestamp desde cuándo está degradada.
        /// RAZÓN: Para calcular duración de la degradación.
        /// </summary>
        [Column("degradation_since")]
        public DateTime? DegradationSince { get; set; }

        /// <summary>
        /// Timestamp de última actualización de estas stats.
        /// </summary>
        [Column("last_updated")]
        public DateTime LastUpdated { get; set; }
    }
}
