using System;
using log4net;
using PrinterServices.Api;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Monitoring;
using PrinterServices.Queue;
using PrinterServices.Workers;

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
        private StatusMonitor _statusMonitor;

        public void Start()
        {
            Log.Info("═══════════════════════════════════════════════");
            Log.Info("  PrinterServices v1.0 — Iniciando...");
            Log.Info("═══════════════════════════════════════════════");

            try
            {
                // 1. Inicializar base de datos SQLite
                _db = PrinterServiceDb.GetInstance();
                Log.Info("[DB] Base de datos inicializada: " + _db.DatabasePath);

                // 2. Inicializar ConfigManager (centralizado, BD-backed)
                _configManager = ConfigManager.GetInstance(_db);
                Log.Info("[CONFIG] Configuración centralizada cargada");

                // 3. Inicializar cola de impresión
                _jobManager = new PrintJobManager(_db);
                _jobManager.RecoverPending();
                Log.Info("[QUEUE] Cola de impresión inicializada");

                // 4. Inicializar worker de impresión
                _printWorker = new PrintWorker(_jobManager, _db);
                _printWorker.Start();
                Log.Info("[WORKER] Worker de impresión iniciado");

                // 5. Inicializar HTTP API
                int httpPort = _configManager.GetInt("HttpPort", 8090);
                _httpApiServer = new HttpApiServer(httpPort, _db, _jobManager, _configManager);
                _httpApiServer.Start();
                Log.InfoFormat("[HTTP] Servidor escuchando en puerto {0}", httpPort);

                // 6. Inicializar StatusMonitor (DLE EOT)
                _statusMonitor = new StatusMonitor(_db, _jobManager);
                _statusMonitor.Start();

                // TODO Fase 5: gRPC NotificationManager
                // TODO Fase 6: UdpDiscoveryServer
                // TODO Fase 8: NetworkWatcher (proceso paralelo)

                Log.Info("═══════════════════════════════════════════════");
                Log.Info("  PrinterServices — Listo");
                Log.InfoFormat("  HTTP: http://localhost:{0}/api/health", httpPort);
                Log.InfoFormat("  POST: http://localhost:{0}/api/print/comanda", httpPort);
                Log.InfoFormat("  GET:  http://localhost:{0}/api/config", httpPort);
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
