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
        private NetworkSpeedWorker _networkSpeedWorker;
        private NetworkDiscoveryWorker _networkDiscoveryWorker;

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

                // 11. Health Dashboard workers
                _networkSpeedWorker = new NetworkSpeedWorker(_db, _configManager);
                _networkSpeedWorker.Start();
                Api.ApiRouter.SpeedWorker = _networkSpeedWorker;
                _networkDiscoveryWorker = new NetworkDiscoveryWorker(_db, _configManager);
                _networkDiscoveryWorker.Start();
                Api.ApiRouter.DiscoveryWorker = _networkDiscoveryWorker;

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

                if (_networkSpeedWorker != null) _networkSpeedWorker.Stop();
                if (_networkDiscoveryWorker != null) _networkDiscoveryWorker.Stop();

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

    /// <summary>
    /// Worker: Mide velocidad de red cada 2 horas o manualmente.
    /// Inline en este archivo porque los archivos nuevos creados en macOS no compilan via C:\Mac\Home\.
    /// </summary>
    public class NetworkSpeedWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkSpeedWorker));
        private readonly Data.PrinterServiceDb _db;
        private readonly Config.ConfigManager _config;
        private System.Threading.CancellationTokenSource _cts;
        private System.Threading.Tasks.Task _workerTask;
        private readonly System.Threading.SemaphoreSlim _manualSignal = new System.Threading.SemaphoreSlim(0, 1);

        public NetworkSpeedWorker(Data.PrinterServiceDb db, Config.ConfigManager config) { _db = db; _config = config; }

        public void Start()
        {
            if (_workerTask != null) return;
            _cts = new System.Threading.CancellationTokenSource();
            _workerTask = System.Threading.Tasks.Task.Factory.StartNew(() => RunLoop(_cts.Token), _cts.Token, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
            Log.Info("[SPEED-WORKER] Iniciado");
        }

        public void Stop()
        {
            if (_cts != null) _cts.Cancel();
            if (_workerTask != null) try { _workerTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
            Log.Info("[SPEED-WORKER] Detenido");
        }

        public void MeasureNow() { try { if (_manualSignal.CurrentCount == 0) _manualSignal.Release(); } catch { } }

        private void RunLoop(System.Threading.CancellationToken ct)
        {
            try { System.Threading.Tasks.Task.Delay(30000, ct).Wait(ct); } catch { return; }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var networkCurrent = _db.Table<Data.Models.NetworkCurrentEntity>().FirstOrDefault(n => n.Id == 1);
                    string gatewayIp = networkCurrent?.GatewayIp;
                    int latencyMs = 0;
                    if (!string.IsNullOrEmpty(gatewayIp))
                    {
                        using (var ping = new System.Net.NetworkInformation.Ping())
                        {
                            long totalMs = 0; int ok = 0;
                            for (int i = 0; i < 3; i++) { var r = ping.Send(gatewayIp, 2000); if (r.Status == System.Net.NetworkInformation.IPStatus.Success) { totalMs += r.RoundtripTime; ok++; } }
                            latencyMs = ok > 0 ? (int)(totalMs / ok) : -1;
                        }
                    }
                    double downloadKbps = 0;
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew(); long bytes = 0;
                        var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://instaladores.restaurant.pe/printer.zip");
                        req.Timeout = 10000; req.ReadWriteTimeout = 10000;
                        using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                        using (var stream = resp.GetResponseStream())
                        {
                            var buf = new byte[8192]; int read;
                            while ((read = stream.Read(buf, 0, buf.Length)) > 0) { bytes += read; if (sw.ElapsedMilliseconds > 5000) break; }
                        }
                        sw.Stop();
                        if (sw.ElapsedMilliseconds > 100 && bytes > 1024) downloadKbps = Math.Round((bytes * 8.0) / (sw.ElapsedMilliseconds / 1000.0) / 1024.0, 1);
                    }
                    catch (Exception ex) { Log.Warn("[SPEED-WORKER] Error descarga: " + ex.Message); }
                    _db.Insert(new Data.Models.NetworkSpeedLogEntity { DownloadSpeedKbps = downloadKbps, LatencyMs = latencyMs, GatewayIp = gatewayIp ?? "N/A", NetworkId = networkCurrent?.NetworkId ?? "N/A", MeasuredAt = DateTime.Now.ToString("o") });
                    Log.InfoFormat("[SPEED-WORKER] {0:F1} Kbps, {1}ms", downloadKbps, latencyMs);
                }
                catch (Exception ex) { Log.Error("[SPEED-WORKER] Error: " + ex.Message); }
                try { int mins = _config.GetInt("NetworkSpeedIntervalMinutes", 120); _manualSignal.Wait(TimeSpan.FromMinutes(mins), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Worker: Descubre dispositivos en la red cada 1 hora o manualmente.
    /// </summary>
    public class NetworkDiscoveryWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkDiscoveryWorker));
        private readonly Data.PrinterServiceDb _db;
        private readonly Config.ConfigManager _config;
        private System.Threading.CancellationTokenSource _cts;
        private System.Threading.Tasks.Task _workerTask;
        private readonly System.Threading.SemaphoreSlim _manualSignal = new System.Threading.SemaphoreSlim(0, 1);

        public NetworkDiscoveryWorker(Data.PrinterServiceDb db, Config.ConfigManager config) { _db = db; _config = config; }

        public void Start()
        {
            if (_workerTask != null) return;
            _cts = new System.Threading.CancellationTokenSource();
            _workerTask = System.Threading.Tasks.Task.Factory.StartNew(() => RunLoop(_cts.Token), _cts.Token, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
            Log.Info("[DISCOVERY] Iniciado");
        }

        public void Stop()
        {
            if (_cts != null) _cts.Cancel();
            if (_workerTask != null) try { _workerTask.Wait(TimeSpan.FromSeconds(10)); } catch { }
            Log.Info("[DISCOVERY] Detenido");
        }

        /// <summary>Ejecuta scan inmediato (llamado desde endpoint HTTP, corre en el request thread)</summary>
        public void ScanNow()
        {
            try { PerformScan(System.Threading.CancellationToken.None); }
            catch (Exception ex) { Log.Error("[DISCOVERY] Error en ScanNow: " + ex.Message); }
        }

        private void RunLoop(System.Threading.CancellationToken ct)
        {
            // Primer scan despues de 60 segundos
            try { _manualSignal.Wait(TimeSpan.FromSeconds(60), ct); } catch { return; }
            while (!ct.IsCancellationRequested)
            {
                try { PerformScan(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error("[DISCOVERY] Error: " + ex.Message); }
                try { int mins = _config.GetInt("NetworkDiscoveryIntervalMinutes", 60); _manualSignal.Wait(TimeSpan.FromMinutes(mins), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void PerformScan(System.Threading.CancellationToken ct)
        {
            var nc = _db.Table<Data.Models.NetworkCurrentEntity>().FirstOrDefault(n => n.Id == 1);
            string gwMac = nc?.GatewayMac ?? "";
            string netId = nc?.NetworkId ?? "";
            string myIp = nc?.PrinterServiceIp ?? "";
            string mask = nc?.SubnetMask ?? "255.255.255.0";
            string now = DateTime.Now.ToString("o");
            int newCount = 0, updCount = 0;

            // Paso 1: Ping sweep paralelo para forzar al OS a poblar la tabla ARP
            // Sin esto, solo aparecen dispositivos con los que ya hubo comunicacion
            if (!string.IsNullOrEmpty(myIp))
            {
                PingSweep(myIp, mask, ct);
            }

            // Paso 2: Leer tabla ARP completa (ahora incluye dispositivos descubiertos por el ping sweep)
            var arpTable = PrinterServices.Core.Network.ArpHelper.GetArpTable();
            Log.InfoFormat("[DISCOVERY] Tabla ARP tiene {0} entradas (post-sweep)", arpTable.Count);

            if (arpTable.Count == 0) { Log.Warn("[DISCOVERY] Tabla ARP vacia"); return; }

            try { _db.Execute("UPDATE devices_on_network SET is_online = 0"); } catch { }

            foreach (var entry in arpTable)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    string ip = entry.Key;
                    string mac = BitConverter.ToString(entry.Value.GetAddressBytes()).Replace("-", ":");
                    // Omitir broadcast y multicast
                    if (mac.StartsWith("FF:FF:FF") || mac.StartsWith("01:00:5E")) continue;

                    string hostname = null;
                    try { var he = System.Net.Dns.GetHostEntry(ip); if (he != null && !string.IsNullOrEmpty(he.HostName) && he.HostName != ip) hostname = he.HostName; } catch { }
                    string vendor = Services.Network.OuiLookup.GetVendor(mac);
                    string devType = Services.Network.OuiLookup.InferDeviceType(vendor);

                    var existing = _db.Table<Data.Models.DeviceOnNetworkEntity>().FirstOrDefault(d => d.MacAddress == mac);
                    if (existing != null)
                    {
                        existing.IpAddress = ip; existing.LastSeenAt = now; existing.IsOnline = 1;
                        existing.NetworkId = netId; existing.GatewayMac = gwMac;
                        if (hostname != null) existing.Hostname = hostname;
                        if (vendor != null) existing.Vendor = vendor;
                        existing.DeviceType = devType;
                        _db.Update(existing); updCount++;
                    }
                    else
                    {
                        _db.Insert(new Data.Models.DeviceOnNetworkEntity { IpAddress = ip, MacAddress = mac, Hostname = hostname, Vendor = vendor, DeviceType = devType, FirstSeenAt = now, LastSeenAt = now, IsOnline = 1, NetworkId = netId, GatewayMac = gwMac });
                        newCount++;
                    }
                }
                catch { }
            }
            Log.InfoFormat("[DISCOVERY] {0} nuevos, {1} actualizados de {2} entradas ARP", newCount, updCount, arpTable.Count);
        }

        /// <summary>
        /// Ping sweep paralelo: envia ICMP echo a todas las IPs de la subred.
        /// Esto fuerza al OS a enviar ARP requests y poblar la tabla ARP con dispositivos desconocidos.
        /// Timeout corto (50ms) + paralelo = ~3-5 segundos para /24 completa.
        /// </summary>
        private void PingSweep(string myIp, string subnetMask, System.Threading.CancellationToken ct)
        {
            try
            {
                var ipb = System.Net.IPAddress.Parse(myIp).GetAddressBytes();
                var mb = System.Net.IPAddress.Parse(subnetMask).GetAddressBytes();
                var nb = new byte[4];
                for (int i = 0; i < 4; i++) nb[i] = (byte)(ipb[i] & mb[i]);
                uint na = (uint)(nb[0] << 24 | nb[1] << 16 | nb[2] << 8 | nb[3]);

                // Calcular cantidad de hosts (max 254 para /24)
                var bb = new byte[4];
                for (int i = 0; i < 4; i++) bb[i] = (byte)(nb[i] | ~mb[i]);
                uint ba = (uint)(bb[0] << 24 | bb[1] << 16 | bb[2] << 8 | bb[3]);
                uint cnt = ba - na - 1; if (cnt > 254) cnt = 254;

                Log.InfoFormat("[DISCOVERY] Ping sweep: {0} hosts en red {1}", cnt, myIp);

                // Ping paralelo con Parallel.ForEach (max 20 threads simultaneos)
                var ips = new System.Collections.Generic.List<string>();
                for (uint i = 1; i <= cnt; i++)
                {
                    uint a = na + i;
                    ips.Add(string.Format("{0}.{1}.{2}.{3}", (a >> 24) & 0xFF, (a >> 16) & 0xFF, (a >> 8) & 0xFF, a & 0xFF));
                }

                var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 20, CancellationToken = ct };
                System.Threading.Tasks.Parallel.ForEach(ips, options, ip =>
                {
                    try
                    {
                        using (var ping = new System.Net.NetworkInformation.Ping())
                        {
                            ping.Send(ip, 50); // Timeout 50ms — solo necesitamos que el OS envie ARP
                        }
                    }
                    catch { }
                });

                // Esperar 500ms para que el OS procese las respuestas ARP
                System.Threading.Thread.Sleep(500);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("[DISCOVERY] Error en ping sweep: " + ex.Message); }
        }
    }
}
