using System;
using log4net;
using PrinterServices.Api;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Grpc;
using PrinterServices.Monitoring;
using PrinterServices.Notifications;
using PrinterServices.Queue;
using PrinterServices.Discovery;
using PrinterServices.Workers;
using PrinterServices.Services.Network;

namespace PrinterServices
{
    public class PrinterServicesHost
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterServicesHost));

        private HttpApiServer _httpApiServer;
        private PrinterServiceDb _db;
        private ConfigManager _configManager;
        private PrintJobManager _jobManager;
        private PrintWorker _printWorker;
        private ArpScanWorker _arpWorker;             // Worker independiente para búsquedas ARP (proceso paralelo)
        private NotificationRetryWorker _notifRetryWorker; // Worker para reintentar notificaciones a QuipuNetX
        private StatusMonitor _statusMonitor;
        private NotificationManager _notifManager;
        private GrpcNotificationServer _grpcServer;
        private UdpDiscoveryServer _udpDiscovery;     // Fase 6: auto-descubrimiento UDP
        private NetworkWatcher _networkWatcher;       // Fase 8: monitoreo bidireccional de red
        private DbMaintenanceWorker _dbMaintenanceWorker; // Purga logs > 30 días cada hora

        public void Start()
        {
            Log.Info("═══════════════════════════════════════════════");
            Log.InfoFormat("  PrinterServices v{0} — Iniciando...", Core.ServiceVersion.FullVersion);
            Log.Info("═══════════════════════════════════════════════");

            try
            {
                // 1. Inicializar base de datos SQLite
                _db = PrinterServiceDb.GetInstance();
                Log.Info("[DB] Base de datos inicializada: " + _db.DatabasePath);

                // 1.1. Inicializar worker de mantenimiento de BD (purga logs > 30 días)
                // Se ejecuta inmediatamente al inicio y luego cada hora
                _dbMaintenanceWorker = new DbMaintenanceWorker(_db);
                _dbMaintenanceWorker.Start();
                Log.Info("[DB-MAINTENANCE] Worker de mantenimiento de BD iniciado (purga cada 1h)");

                // 2. Inicializar ConfigManager (centralizado, BD-backed)
                _configManager = ConfigManager.GetInstance(_db);
                Log.Info("[CONFIG] Configuración centralizada cargada");

                // 3. Inicializar cola de impresión
                _jobManager = new PrintJobManager(_db);
                _jobManager.RecoverPending();
                Log.Info("[QUEUE] Cola de impresión inicializada");

                // 4. Inicializar servicios SOLID para Fase 8 (monitoreo de red y latencias)
                // RAZÓN: Instanciar antes de PrintWorker (que los necesita como dependencias)
                var networkConfigCapture = new NetworkConfigCapture();
                var networkSnapshotManager = new NetworkSnapshotManager(_db);
                var networkHealthChecker = new NetworkHealthChecker(_db);
                var latencyMeasurement = new LatencyMeasurement(_db);
                var degradationDetector = new DegradationDetector(_db);

                // 4.1. Inicializar NetworkWatcher con dependencias inyectadas (DIP)
                _networkWatcher = new NetworkWatcher(_db, networkConfigCapture, 
                    networkSnapshotManager, networkHealthChecker);
                _networkWatcher.Start();
                Log.Info("[NET-WATCHER] NetworkWatcher iniciado (monitoreo bidireccional)");

                // 4.2. Fase 23: Crear notificador de callbacks de estado de jobs para QuipuNetX
                var jobStatusCallbackNotifier = new Notifications.JobStatusCallbackNotifier(_db, _configManager);

                // 4.3. Inicializar worker de impresión con servicios de Fase 8 + Fase 23 inyectados
                _printWorker = new PrintWorker(_jobManager, _db, latencyMeasurement,
                    degradationDetector, networkHealthChecker, _networkWatcher, jobStatusCallbackNotifier);
                _printWorker.Start();
                Log.Info("[WORKER] Worker de impresión iniciado (con instrumentación de latencias)");

                // 5. Inicializar HTTP API
                int httpPort = _configManager.GetInt("HttpPort", 8090);
                _httpApiServer = new HttpApiServer(httpPort, _db, _jobManager, _configManager);
                _httpApiServer.Start();
                Log.InfoFormat("[HTTP] Servidor escuchando en puerto {0}", httpPort);

                // 6. Inicializar ArpScanWorker (proceso paralelo para búsquedas ARP)
                // ARQUITECTURA: Corre en hilo independiente, NO bloquea StatusMonitor
                // StatusMonitor delega búsquedas ARP (~500ms) a este worker
                // Si impresora vuelve online → StatusMonitor cancela búsqueda en progreso
                _arpWorker = new ArpScanWorker(_db);
                _arpWorker.Start();
                Log.Info("[ARP-WORKER] Worker de búsqueda ARP iniciado (event-driven)");

                // 6.1. Inicializar NotificationRetryWorker (sincronización con QuipuNetX)
                // Reintenta enviar notificaciones de cambio de IP pendientes cada N segundos
                // Si QuipuNetX está offline cuando se detecta cambio de IP, se persiste en BD
                // Este worker reintenta automáticamente hasta que QuipuNetX responda
                _notifRetryWorker = new NotificationRetryWorker(_db, _configManager);
                _notifRetryWorker.Start();
                Log.Info("[NOTIF-RETRY] Worker de reintentos de notificaciones iniciado");

                // 7. Inicializar servicios SOLID para monitoreo de impresoras (PRINCIPIO DIP)
                // RAZÓN: StatusMonitor depende de abstracciones, no de implementaciones concretas
                // BENEFICIO: Testing con mocks, intercambiabilidad, mantenibilidad
                
                // 7.1. Instanciar servicio de enriquecimiento de MACs (SRP)
                // RESPONSABILIDAD: Obtener MAC vía ARP y normalizar formatos
                var macEnricher = new Services.Printers.PrinterMacEnricher(_db);
                
                // 7.2. Instanciar servicio de agrupamiento por dispositivo físico (SRP)
                // RESPONSABILIDAD: Agrupar impresoras con misma MAC (BARRA, BARRA 2)
                var deviceGrouper = new Services.Printers.PhysicalDeviceGrouper();
                
                // 7.3. Instanciar servicio de sincronización de estado (SRP)
                // RESPONSABILIDAD: Propagar estado entre impresoras del mismo dispositivo
                var stateSync = new Services.Printers.PrinterStateSync(_db);
                
                // 7.4. Inicializar StatusMonitor con dependencias inyectadas (DIP)
                // RAZÓN: Recibe todas las dependencias desde fuera, no las crea internamente
                _statusMonitor = new StatusMonitor(_db, _jobManager, _arpWorker, 
                    macEnricher, deviceGrouper, stateSync, jobStatusCallbackNotifier);
                _statusMonitor.Start();

                // 7.5. Enlazar NetworkWatcher → StatusMonitor para trigger inmediato
                // RAZÓN: Cuando NetworkWatcher detecta transición changed→healthy (red volvió),
                // despierta a StatusMonitor para re-verificar impresoras sin esperar intervalo de 3s.
                _networkWatcher.SetStatusMonitor(_statusMonitor);
                Log.Info("[HOST] NetworkWatcher enlazado con StatusMonitor (reconexión rápida habilitada)");

                // 8. Inicializar NotificationManager (dispatcher central de notificaciones)
                // Singleton que gestiona suscriptores gRPC y difunde eventos de impresión/estado.
                // Debe inicializarse ANTES del gRPC server y DESPUÉS de la BD.
                _notifManager = NotificationManager.GetInstance(_db);
                Log.Info("[NOTIF] NotificationManager inicializado");

                // 9. Inicializar gRPC server (Grpc.Core 2.46.6, puerto configurable)
                // Expone 3 RPCs: SuscribirNotificacionesServidor, SuscribirNotificacionesCliente, GetStatusPrinters
                // Los Quipunet.exe (Servidor y Clientes) se conectan aquí para recibir notificaciones push.
                _grpcServer = new GrpcNotificationServer(_db, _notifManager, _jobManager);
                _grpcServer.Start(); // Abre socket en GrpcBindAddress:GrpcPort (default 0.0.0.0:50051)
                int grpcPort = _configManager.GetInt("GrpcPort", 50051);

                // 10. Inicializar UDP Discovery Server (auto-descubrimiento en red local)
                // Quipunet.exe envía broadcast "QUIPU_PRINTER_DISCOVERY" en UDP 9999.
                // PrinterServices responde unicast con IP|HttpPort|GrpcPort.
                // Configurable: UdpDiscoveryEnabled (bool), UdpDiscoveryPort (int).
                _udpDiscovery = new UdpDiscoveryServer();
                _udpDiscovery.Start();
                int udpPort = _configManager.GetInt("UdpDiscoveryPort", 9999);

                Log.Info("═══════════════════════════════════════════════");
                Log.Info("  PrinterServices — Listo");
                Log.InfoFormat("  HTTP: http://localhost:{0}/api/health", httpPort);
                Log.InfoFormat("  POST: http://localhost:{0}/api/print/comanda", httpPort);
                Log.InfoFormat("  GET:  http://localhost:{0}/api/config", httpPort);
                Log.InfoFormat("  gRPC: localhost:{0}", grpcPort);
                Log.InfoFormat("  UDP:  broadcast:{0} (discovery)", udpPort);
                Log.Info("═══════════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Log.Fatal("Error al iniciar PrinterServices", ex);
                throw;
            }
        }

        public void Stop()
        {
            Log.Info("PrinterServices — Deteniendo...");

            try
            {
                if (_httpApiServer != null)
                {
                    _httpApiServer.Stop();
                    Log.Info("[HTTP] Servidor detenido");
                }

                if (_printWorker != null)
                {
                    _printWorker.Stop();
                    Log.Info("[WORKER] Worker detenido");
                }

                if (_statusMonitor != null)
                {
                    _statusMonitor.Stop();
                    Log.Info("[MONITOR] StatusMonitor detenido");
                }

                // Detener ArpScanWorker (cancela búsquedas activas y cierra hilo)
                if (_arpWorker != null)
                {
                    _arpWorker.Stop();
                    Log.Info("[ARP-WORKER] Worker de búsqueda ARP detenido");
                }

                // Detener NotificationRetryWorker (detiene reintentos de notificaciones a QuipuNetX)
                if (_notifRetryWorker != null)
                {
                    _notifRetryWorker.Stop();
                    Log.Info("[NOTIF-RETRY] Worker de reintentos de notificaciones detenido");
                }

                // Detener NetworkWatcher (Fase 8: cancela monitoreo de red)
                if (_networkWatcher != null)
                {
                    _networkWatcher.Stop();
                    Log.Info("[NET-WATCHER] NetworkWatcher detenido");
                }

                // Detener UDP Discovery (cancela el loop de escucha)
                if (_udpDiscovery != null)
                {
                    _udpDiscovery.Stop();
                }

                // Detener gRPC server (graceful shutdown, espera hasta 5s para cerrar streams)
                if (_grpcServer != null)
                {
                    _grpcServer.Stop();
                    Log.Info("[gRPC] Servidor gRPC detenido");
                }

                // Detener DbMaintenanceWorker (purga de logs antiguos)
                if (_dbMaintenanceWorker != null)
                {
                    _dbMaintenanceWorker.Stop();
                    Log.Info("[DB-MAINTENANCE] Worker de mantenimiento detenido");
                }

                if (_db != null)
                {
                    _db.Close();
                    Log.Info("[DB] Base de datos cerrada");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error al detener PrinterServices", ex);
            }

            Log.Info("PrinterServices — Detenido.");
        }
    }
}
