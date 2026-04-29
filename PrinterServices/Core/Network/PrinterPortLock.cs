using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace PrinterServices.Core.Network
{
    /// <summary>
    /// Mutex por IP de impresora que serializa TODO acceso TCP al puerto de impresión
    /// (normalmente 9100) de un dispositivo físico.
    ///
    /// RAZÓN: Las impresoras térmicas ESC/POS económicas y emulaciones no-Epson
    /// (CUSTOM/POS, Star, etc.) típicamente aceptan múltiples conexiones TCP simultáneas
    /// a port 9100 Y serializan los bytes entrantes sin distinguir de qué socket vienen.
    /// Esto provoca que bytes del pre-check DLE EOT del StatusMonitor (o de un segundo
    /// print job) se inyecten en medio de la rasterización del bitmap del job activo,
    /// produciendo cortes en puntos aleatorios del ticket.
    ///
    /// Este lock garantiza que por cada IP solo una operación TCP a port 9100 esté
    /// activa a la vez: pre-check, send del bitmap, wait post-print, y los checks
    /// periódicos del StatusMonitor son mutuamente excluyentes.
    ///
    /// USO:
    ///   using (var _ = await PrinterPortLock.AcquireAsync(ip, ct)) { ... }   // espera
    ///   var h = await PrinterPortLock.TryAcquireAsync(ip, 5000, ct);         // timeout
    ///   if (h != null) { try { ... } finally { h.Dispose(); } }
    ///
    /// El lock es lazy por IP: se crea el primer SemaphoreSlim recién cuando se pide
    /// esa IP. No se limpia — un máximo razonable de impresoras por instalación
    /// (decenas) hace el costo de memoria despreciable.
    /// </summary>
    public static class PrinterPortLock
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterPortLock));

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// Adquiere el lock de la IP, esperando indefinidamente (respeta CancellationToken).
        /// Si la IP está vacía retorna un handle no-op (no se aplica lock).
        /// </summary>
        public static async Task<PrinterPortLockHandle> AcquireAsync(string ip, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ip)) return PrinterPortLockHandle.NoOp;

            string key = Normalize(ip);
            var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await sem.WaitAsync(ct).ConfigureAwait(false);
            return new PrinterPortLockHandle(key, sem);
        }

        /// <summary>
        /// Intenta adquirir el lock con timeout. Retorna null si expira sin adquirirlo
        /// (por ejemplo, hay un print en curso que lo retiene por segundos).
        /// El caller decide si skipea la operación o reintenta luego.
        /// </summary>
        public static async Task<PrinterPortLockHandle> TryAcquireAsync(string ip, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ip)) return PrinterPortLockHandle.NoOp;

            string key = Normalize(ip);
            var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            bool acquired = await sem.WaitAsync(timeoutMs, ct).ConfigureAwait(false);
            if (!acquired) return null;

            return new PrinterPortLockHandle(key, sem);
        }

        private static string Normalize(string ip)
        {
            return ip.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Handle retornado por PrinterPortLock. Libera el semáforo al Dispose.
    /// Es seguro llamar Dispose varias veces.
    /// </summary>
    public sealed class PrinterPortLockHandle : IDisposable
    {
        public static readonly PrinterPortLockHandle NoOp = new PrinterPortLockHandle(null, null);

        private readonly string _key;
        private SemaphoreSlim _sem;

        internal PrinterPortLockHandle(string key, SemaphoreSlim sem)
        {
            _key = key;
            _sem = sem;
        }

        public void Dispose()
        {
            var s = System.Threading.Interlocked.Exchange(ref _sem, null);
            if (s != null)
            {
                try { s.Release(); }
                catch (SemaphoreFullException) { /* ya liberado: idempotente */ }
            }
        }
    }
}
