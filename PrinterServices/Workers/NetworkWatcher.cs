using System;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Services.Network;

namespace PrinterServices.Workers
{
    /// <summary>
    /// WORKER: Monitoreo bidireccional de red (PrinterServices + impresoras).
    /// PROPÓSITO: Detectar cambios de red y degradación de latencia.
    /// PRINCIPIO DIP: Depende de abstracciones (interfaces), no de implementaciones concretas.
    /// PRINCIPIO SRP: Solo coordina servicios especializados, no hace trabajo pesado.
    /// </summary>
    public class NetworkWatcher
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkWatcher));

        private readonly PrinterServiceDb _db;
        private readonly INetworkConfigCapture _configCapture;
        private readonly INetworkSnapshotManager _snapshotManager;
        private readonly INetworkHealthChecker _healthChecker;

        private CancellationTokenSource _cts;
        private Task _workerTask;

        // RAZÓN: Referencia a StatusMonitor para despertar checks inmediatos cuando la red vuelve.
        // Se inyecta después de construcción (SetStatusMonitor) porque StatusMonitor se crea después.
        private Monitoring.StatusMonitor _statusMonitor;

        // RAZÓN: Tracking de estado anterior para detectar transiciones (changed→healthy)
        private string _previousHealthStatus = "unknown";

        // RAZÓN: Intervalo rápido (2s) cuando red cambió, para detectar retorno más rápido
        private const int FAST_POLL_INTERVAL_SECONDS = 2;

        /// <summary>
        /// Constructor con inyección de dependencias (DIP).
        /// RAZÓN: Recibe abstracciones, no implementaciones concretas.
        /// BENEFICIO: Testeable con mocks, intercambiable.
        /// </summary>
        public NetworkWatcher(
            PrinterServiceDb db,
            INetworkConfigCapture configCapture,
            INetworkSnapshotManager snapshotManager,
            INetworkHealthChecker healthChecker)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _configCapture = configCapture ?? throw new ArgumentNullException(nameof(configCapture));
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
        }

        /// <summary>
        /// Inyectar referencia a StatusMonitor después de construcción.
        /// RAZÓN: StatusMonitor se crea después de NetworkWatcher en PrinterServicesHost,
        /// pero necesitamos despertarlo cuando la red vuelve.
        /// </summary>
        public void SetStatusMonitor(Monitoring.StatusMonitor monitor)
        {
            _statusMonitor = monitor;
            Log.Info("[NET-WATCHER] StatusMonitor enlazado para trigger inmediato");
        }

        /// <summary>
        /// Inicia el worker en thread dedicado (LongRunning).
        /// RAZÓN: Mismo patrón que StatusMonitor y PrintWorker.
        /// </summary>
        public void Start()
        {
            if (_workerTask != null)
            {
                Log.Warn("[NET-WATCHER] Ya está iniciado, ignorando");
                return;
            }

            _cts = new CancellationTokenSource();

            // RAZÓN: TaskCreationOptions.LongRunning → hilo dedicado del ThreadPool
            _workerTask = Task.Factory.StartNew(
                () => RunLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Log.Info("[NET-WATCHER] ✓ Iniciado (monitoreo bidireccional de red)");
        }

        /// <summary>
        /// Detiene el worker de manera controlada.
        /// </summary>
        public void Stop()
        {
            if (_cts != null)
            {
                Log.Info("[NET-WATCHER] Deteniendo...");
                _cts.Cancel();
            }

            if (_workerTask != null)
            {
                try
                {
                    _workerTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    Log.Warn("[NET-WATCHER] Error esperando finalización: " + ex.Message);
                }
            }

            Log.Info("[NET-WATCHER] ✓ Detenido");
        }

        /// <summary>
        /// Loop principal de monitoreo (ejecuta cada 30s).
        /// RAZÓN: Ciclo similar a StatusMonitor, completamente paralelo.
        /// PRINCIPIO: NO toca PrintJobManager, NO bloquea PrintWorker.
        /// </summary>
        private async Task RunLoop(CancellationToken ct)
        {
            // RAZÓN: Obtener intervalo de config (default 30s)
            int intervalSeconds = ConfigManager.Instance.GetInt("NetworkWatcherIntervalSeconds", 30);

            Log.InfoFormat("[NET-WATCHER] Intervalo de monitoreo: {0}s", intervalSeconds);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ═══════════════════════════════════════════════════════════════
                    // PASO 1: CAPTURAR configuración de red actual
                    // ═══════════════════════════════════════════════════════════════
                    var currentConfig = _configCapture.CaptureCurrentConfig();

                    if (currentConfig == null)
                    {
                        Log.WarnFormat("[NET-WATCHER] No se pudo capturar config actual, reintentando en {0}s", intervalSeconds);
                        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
                        continue;
                    }

                    // ═══════════════════════════════════════════════════════════════
                    // PASO 2: COMPARAR con última red conocida buena
                    // ═══════════════════════════════════════════════════════════════
                    var lastKnownGood = _snapshotManager.GetLastKnownGoodNetwork();

                    string healthStatus = _healthChecker.CheckNetworkHealth(currentConfig, lastKnownGood);

                    // ═══════════════════════════════════════════════════════════════
                    // PASO 3: ACTUALIZAR estado actual en BD (singleton)
                    // ═══════════════════════════════════════════════════════════════
                    _healthChecker.UpdateCurrentNetworkStatus(currentConfig, healthStatus);

                    // ═══════════════════════════════════════════════════════════════
                    // PASO 4: ACCIONES según estado detectado
                    // ═══════════════════════════════════════════════════════════════
                    if (healthStatus == "changed")
                    {
                        // ❌ PrinterServices CAMBIÓ DE RED
                        // FIX: Solo disparar HandleNetworkChanged en la TRANSICIÓN healthy→changed,
                        // no en cada tick mientras sigue "changed". El loop con FAST_POLL_INTERVAL_SECONDS=2s
                        // hacía que este método se ejecute cada 2s y resetee estado_online constantemente,
                        // generando falsas transiciones OFFLINE→ONLINE sin los ONLINE→OFFLINE
                        // correspondientes (bug de flapping documentado en
                        // vibe_engeneering_print_robustness.md §2.2).
                        if (_previousHealthStatus != "changed")
                        {
                            HandleNetworkChanged(currentConfig, lastKnownGood);
                            // Despertar al StatusMonitor para que re-evalúe cada impresora
                            // individualmente con transiciones correctamente logueadas.
                            if (_statusMonitor != null)
                            {
                                _statusMonitor.TriggerImmediateCheck("Red cambió — re-evaluar impresoras individualmente");
                            }
                        }
                        else
                        {
                            Log.Debug("[NET-WATCHER] Red sigue en estado 'changed', esperando recuperación...");
                        }
                    }
                    else if (healthStatus == "healthy")
                    {
                        // ✅ Red correcta
                        // RAZÓN: Detectar transición changed→healthy (red volvió)
                        if (_previousHealthStatus == "changed")
                        {
                            Log.Info("[NET-WATCHER] ✅ RED RESTAURADA — transición changed→healthy");
                            // Despertar StatusMonitor INMEDIATAMENTE para re-verificar impresoras
                            if (_statusMonitor != null)
                            {
                                _statusMonitor.TriggerImmediateCheck("Red restaurada (NetworkWatcher detectó changed→healthy)");
                            }
                        }
                        else
                        {
                            Log.Debug("[NET-WATCHER] Red saludable, continuando monitoreo");
                        }
                    }
                    else if (healthStatus == "unknown")
                    {
                        // ⚠ Primera ejecución - auto-aprender red actual
                        Log.Info("[NET-WATCHER] Primera ejecución, registrando red actual como buena");
                        _snapshotManager.RecordSuccessfulPrint(currentConfig);
                        _healthChecker.UpdateCurrentNetworkStatus(currentConfig, "healthy");
                    }

                    // RAZÓN: Guardar estado actual para detectar transiciones en próximo ciclo
                    _previousHealthStatus = healthStatus;

                    // ═══════════════════════════════════════════════════════════════
                    // RAZÓN: Si red cambió, polling rápido (2s) para detectar retorno rápido.
                    // Si red OK, polling normal (30s) para no consumir CPU innecesariamente.
                    // ═══════════════════════════════════════════════════════════════
                    int effectiveInterval = (healthStatus == "changed")
                        ? FAST_POLL_INTERVAL_SECONDS
                        : intervalSeconds;
                    await Task.Delay(effectiveInterval * 1000, ct);
                }
                catch (TaskCanceledException)
                {
                    // RAZÓN: Cancelación normal, salir del loop
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("[NET-WATCHER] Error en loop: " + ex.Message, ex);
                    // RAZÓN: No terminar el worker por un error, reintentar
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }

            Log.Info("[NET-WATCHER] Loop finalizado");
        }

        /// <summary>
        /// Maneja detección de cambio de red.
        /// RAZÓN: PrinterServices se movió a otra red física.
        /// </summary>
        private void HandleNetworkChanged(
            Core.Network.NetworkConfig currentConfig,
            Core.Network.NetworkConfig lastKnownGood)
        {
            Log.Error("[NET-WATCHER] ⚠⚠⚠ CAMBIO DE RED DETECTADO ⚠⚠⚠");
            Log.ErrorFormat("  Red esperada: {0} (gateway {1})", lastKnownGood?.NetworkId, lastKnownGood?.GatewayMac);
            Log.ErrorFormat("  Red actual: {0} (gateway {1})", currentConfig.NetworkId, currentConfig.GatewayMac);

            if (!string.IsNullOrEmpty(lastKnownGood?.WifiSsid))
            {
                Log.ErrorFormat("  Reconectar a WiFi: {0}", lastKnownGood.WifiSsid);
            }

            // FIX: NO tocamos estado_online desde aquí.
            // Antes: UPDATE printers SET estado_online=0, disponible_para_imprimir=0.
            // Pero eso (a) pisaba en cada tick cuando la red seguía "changed", creando
            // un loop de falsas transiciones OFFLINE→ONLINE sin sus contrapartes,
            // y (b) viola el principio de que SÓLO el StatusMonitor gestiona el
            // estado de conectividad individual de cada impresora (documentado en
            // vibe_engeneering_print_robustness.md §2.2). El StatusMonitor acaba de
            // ser despertado con TriggerImmediateCheck y va a re-evaluar cada impresora
            // individualmente, logueando correctamente las transiciones que se
            // detecten (si la impresora ya no responde por el cambio de red,
            // el check normal la marcará offline + logueará ONLINE→OFFLINE).

            // TODO Fase 8B: Crear NetworkAlert en BD y notificar a QuipuNetX vía gRPC
            // RAZÓN: QuipuNetX debe mostrar alerta al usuario
        }

        /// <summary>
        /// Método público para registrar impresión exitosa.
        /// RAZÓN: PrintWorker llama esto después de cada impresión OK.
        /// </summary>
        public void RecordSuccessfulPrint()
        {
            try
            {
                // RAZÓN: Capturar config actual y registrarla como buena
                var currentConfig = _configCapture.CaptureCurrentConfig();
                if (currentConfig != null)
                {
                    _snapshotManager.RecordSuccessfulPrint(currentConfig);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[NET-WATCHER] Error registrando impresión exitosa: " + ex.Message, ex);
            }
        }
    }
}
