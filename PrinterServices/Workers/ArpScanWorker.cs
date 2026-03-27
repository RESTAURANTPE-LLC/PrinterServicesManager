using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Monitoring;

namespace PrinterServices.Workers
{
    /// <summary>
    /// Worker independiente para búsqueda ARP de impresoras offline.
    /// 
    /// ARQUITECTURA:
    /// - Corre en hilo separado del StatusMonitor (SNMP_EOT)
    /// - StatusMonitor NO bloquea esperando ARP scan (500ms)
    /// - StatusMonitor encola solicitud → ArpScanWorker procesa async
    /// - Si StatusMonitor detecta reconexión → cancela búsqueda en progreso
    /// 
    /// FLUJO:
    /// 1. StatusMonitor detecta OFFLINE → EnqueueScan(impresoraId)
    /// 2. ArpScanWorker toma solicitud y ejecuta scan (500ms)
    /// 3. Si durante scan StatusMonitor detecta ONLINE → CancelScan(impresoraId)
    /// 4. ArpScanWorker verifica cancelación y aborta si ya no es necesario
    /// </summary>
    public class ArpScanWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ArpScanWorker));

        private readonly PrinterServiceDb _db; // Referencia a la base de datos para actualizar impresoras
        private readonly ConcurrentQueue<string> _scanQueue; // Cola de impresoras pendientes de escanear
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeScansCts; // Scans activos (impresoraId → CTS para cancelar)
        private readonly ConcurrentDictionary<string, DateTime> _lastScanTime; // Cooldown: último scan por impresora
        private readonly SemaphoreSlim _signal; // Señal para despertar el worker cuando hay nueva solicitud

        private Task _workerTask; // Tarea del worker en background
        private CancellationTokenSource _lifetimeCts; // CancellationToken para ciclo de vida del worker

        // Cooldown entre scans para la misma impresora (evita saturar red)
        private const int SCAN_COOLDOWN_SECONDS = 30;

        public ArpScanWorker(PrinterServiceDb db)
        {
            _db = db; // Guardar referencia a BD
            _scanQueue = new ConcurrentQueue<string>(); // Inicializar cola de solicitudes
            _activeScansCts = new ConcurrentDictionary<string, CancellationTokenSource>(); // Inicializar dict de scans activos
            _lastScanTime = new ConcurrentDictionary<string, DateTime>(); // Inicializar cooldown
            _signal = new SemaphoreSlim(0); // Inicializar semáforo bloqueado (0 = sin señales)
        }

        /// <summary>
        /// Inicia el worker en un hilo de background (LongRunning).
        /// </summary>
        public void Start()
        {
            if (_workerTask != null) // Verificar si ya está corriendo
            {
                Log.Warn("[ARP-WORKER] Ya estaba iniciado, ignorando Start()");
                return;
            }

            _lifetimeCts = new CancellationTokenSource(); // Crear CTS para ciclo de vida
            _workerTask = Task.Factory.StartNew( // Iniciar tarea en hilo dedicado
                () => WorkerLoop(_lifetimeCts.Token), // Función del worker
                _lifetimeCts.Token, // Token para cancelación
                TaskCreationOptions.LongRunning, // Hilo dedicado (no threadpool)
                TaskScheduler.Default); // Scheduler por defecto

            Log.Info("[ARP-WORKER] Worker de búsqueda ARP iniciado en hilo dedicado");
        }

        /// <summary>
        /// Detiene el worker de forma ordenada.
        /// </summary>
        public void Stop()
        {
            if (_lifetimeCts == null || _workerTask == null) // Verificar si está corriendo
            {
                Log.Warn("[ARP-WORKER] No estaba iniciado, ignorando Stop()");
                return;
            }

            Log.Info("[ARP-WORKER] Deteniendo worker...");
            
            // Cancelar todas las búsquedas activas
            foreach (var kvp in _activeScansCts)
            {
                Log.DebugFormat("[ARP-WORKER] Cancelando scan activo: {0}", kvp.Key);
                kvp.Value.Cancel(); // Solicitar cancelación
            }

            _lifetimeCts.Cancel(); // Cancelar ciclo de vida del worker
            _signal.Release(); // Liberar semáforo para que WorkerLoop salga del await

            try
            {
                _workerTask.Wait(5000); // Esperar hasta 5s a que termine
                Log.Info("[ARP-WORKER] Worker detenido correctamente");
            }
            catch (Exception ex)
            {
                Log.Error("[ARP-WORKER] Error deteniendo worker: " + ex.Message, ex);
            }
            finally
            {
                _lifetimeCts?.Dispose(); // Liberar recursos
                _lifetimeCts = null; // Limpiar referencia
                _workerTask = null; // Limpiar referencia
            }
        }

        /// <summary>
        /// Encolar solicitud de búsqueda ARP para una impresora offline.
        /// Llamado por StatusMonitor cuando detecta desconexión.
        /// NO bloquea, solo encola y señaliza.
        /// </summary>
        /// <param name="impresoraId">ID de impresora para buscar.</param>
        public void EnqueueScan(string impresoraId)
        {
            if (string.IsNullOrEmpty(impresoraId)) return; // Validar ID

            // Verificar si ya hay un scan activo para esta impresora
            if (_activeScansCts.ContainsKey(impresoraId))
            {
                return; // Ya hay un scan activo, no encolar duplicado
            }

            // Cooldown: no escanear la misma impresora más de 1 vez cada 30s
            DateTime lastScan;
            if (_lastScanTime.TryGetValue(impresoraId, out lastScan)
                && (DateTime.Now - lastScan).TotalSeconds < SCAN_COOLDOWN_SECONDS)
            {
                return; // Cooldown activo, esperar
            }

            _lastScanTime[impresoraId] = DateTime.Now; // Registrar timestamp
            _scanQueue.Enqueue(impresoraId); // Encolar solicitud
            _signal.Release(); // Despertar worker (liberar semáforo)

            Log.InfoFormat("======[ ARP SCAN ]====== Solicitud encolada: {0}", impresoraId);
        }

        /// <summary>
        /// Cancelar búsqueda ARP en progreso para una impresora.
        /// Llamado por StatusMonitor cuando detecta que la impresora volvió online.
        /// </summary>
        /// <param name="impresoraId">ID de impresora cuyo scan se debe cancelar.</param>
        public void CancelScan(string impresoraId)
        {
            if (string.IsNullOrEmpty(impresoraId)) return; // Validar ID

            CancellationTokenSource cts; // Variable para CTS
            if (_activeScansCts.TryRemove(impresoraId, out cts)) // Intentar remover scan activo
            {
                cts.Cancel(); // Solicitar cancelación
            }
            // Limpiar cooldown para que pueda escanearse inmediatamente si vuelve a caer
            DateTime _;
            _lastScanTime.TryRemove(impresoraId, out _);
        }

        /// <summary>
        /// Loop principal del worker (event-driven con SemaphoreSlim).
        /// </summary>
        private async Task WorkerLoop(CancellationToken ct)
        {
            Log.Info("[ARP-WORKER] Loop principal iniciado (event-driven)");

            while (!ct.IsCancellationRequested) // Mientras no se cancele
            {
                try
                {
                    // Esperar señal (bloqueante hasta que haya trabajo o se cancele)
                    await _signal.WaitAsync(ct); // Bloquea hasta Release() o cancelación

                    if (ct.IsCancellationRequested) break; // Verificar cancelación después de despertar

                    // Procesar todas las solicitudes en cola
                    while (_scanQueue.TryDequeue(out string impresoraId)) // Mientras haya solicitudes
                    {
                        if (ct.IsCancellationRequested) break; // Verificar cancelación

                        // Procesar solicitud en background (async, no bloquea el loop)
                        _ = ProcessScanAsync(impresoraId, ct); // Fire-and-forget
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelación esperada durante shutdown
                    Log.Debug("[ARP-WORKER] Loop cancelado (shutdown)");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("[ARP-WORKER] Error en loop principal: " + ex.Message, ex);
                    await Task.Delay(1000, ct); // Backoff de 1s antes de reintentar
                }
            }

            Log.Info("[ARP-WORKER] Loop principal finalizado");
        }

        /// <summary>
        /// Procesar una solicitud de scan ARP para una impresora específica.
        /// Ejecuta en background, puede tardar hasta 500ms.
        /// </summary>
        private async Task ProcessScanAsync(string impresoraId, CancellationToken lifetimeCt)
        {
            // Crear CTS específico para este scan (permite cancelación individual)
            var scanCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCt);
            
            // Registrar scan como activo (permite cancelación externa vía CancelScan)
            if (!_activeScansCts.TryAdd(impresoraId, scanCts))
            {
                // Ya existe un scan activo para esta impresora
                Log.DebugFormat("[ARP-WORKER] Scan ya activo para {0}, descartando duplicado", impresoraId);
                scanCts.Dispose(); // Liberar CTS no usado
                return;
            }

            try
            {
                Log.InfoFormat("[ARP-WORKER] 🔍 Iniciando scan ARP para: {0}", impresoraId);

                // Obtener datos de impresora de BD
                var printer = _db.Query<PrinterEntity>(
                    "SELECT * FROM printers WHERE impresora_id = ?", impresoraId).FirstOrDefault();

                if (printer == null) // Validar que exista
                {
                    Log.WarnFormat("[ARP-WORKER] Impresora {0} no encontrada en BD", impresoraId);
                    return;
                }

                // Verificar si tiene MAC registrada (requisito para ARP scan)
                if (string.IsNullOrEmpty(printer.MacAddress))
                {
                    Log.WarnFormat("[ARP-WORKER] Impresora {0} no tiene MAC registrada, scan imposible", impresoraId);
                    return;
                }

                // Ejecutar búsqueda ARP (puede tardar ~500ms, operación costosa)
                var resolved = await Task.Run(() =>
                {
                    // Verificar cancelación antes de iniciar scan
                    if (scanCts.Token.IsCancellationRequested)
                    {
                        Log.InfoFormat("[ARP-WORKER] Scan cancelado antes de iniciar: {0}", impresoraId);
                        return false;
                    }

                    // Ejecutar TryResolveNewIp (sync, usa P/Invoke)
                    // IMPORTANTE: Pasar cancellationToken para abortar scan si impresora vuelve online
                    int timeoutMs = ConfigManager.Instance.GetInt("TcpConnectTimeoutMs", 3000);
                    bool success = PrinterIpResolver.TryResolveNewIp(_db, printer, timeoutMs, scanCts.Token);

                    // Verificar cancelación después de scan
                    if (scanCts.Token.IsCancellationRequested)
                    {
                        Log.InfoFormat("[ARP-WORKER] Scan cancelado después de completar: {0}", impresoraId);
                        return false; // Descartar resultado (impresora ya volvió online)
                    }

                    return success;
                }, scanCts.Token);

                if (resolved)
                {
                    Log.InfoFormat("[ARP-WORKER] ✅ Scan ARP exitoso: {0} (nueva IP encontrada y funcional)", impresoraId);
                }
                else
                {
                    Log.WarnFormat("[ARP-WORKER] ✗ Scan ARP sin resultado: {0} (impresora no encontrada en red)", impresoraId);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelación esperada (impresora volvió online o shutdown)
                Log.InfoFormat("[ARP-WORKER] Scan cancelado: {0}", impresoraId);
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("[ARP-WORKER] Error procesando scan para {0}: {1}", impresoraId, ex.Message);
            }
            finally
            {
                // Limpiar scan activo del diccionario
                CancellationTokenSource _;
                _activeScansCts.TryRemove(impresoraId, out _);
                scanCts.Dispose(); // Liberar recursos del CTS
            }
        }
    }
}
