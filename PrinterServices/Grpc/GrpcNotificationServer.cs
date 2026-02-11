using System;
using Grpc.Core;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Notifications;
using PrinterServices.Queue;

namespace PrinterServices.Grpc
{
    /// <summary>
    /// Host del servidor gRPC (Grpc.Core 2.46.6 — binding C-core para .NET 4.5.2).
    /// 
    /// Expone el servicio PrinterNotification en el puerto configurado (default 50051).
    /// Permite que Quipunet.exe (Servidor y Clientes) se suscriban a notificaciones
    /// de estado de impresión y cambios de estado de impresoras via server-streaming.
    /// 
    /// Ciclo de vida:
    ///   PrinterServicesHost.Start() → new GrpcNotificationServer(...) → Start()
    ///   PrinterServicesHost.Stop()  → Stop() → ShutdownAsync()
    /// 
    /// RPCs disponibles (definidos en printer_notification.proto):
    ///   - SuscribirNotificacionesServidor: server-streaming, recibe TODAS las notificaciones
    ///   - SuscribirNotificacionesCliente:  server-streaming, recibe solo las de SUS comandas
    ///   - GetStatusPrinters:               unary, devuelve estado actual de todas las impresoras
    /// </summary>
    public class GrpcNotificationServer
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(GrpcNotificationServer));

        private Server _server;                              // Instancia del servidor gRPC (Grpc.Core.Server)
        private readonly PrinterServiceDb _db;               // Conexión SQLite (para queries de impresoras/jobs)
        private readonly NotificationManager _notifManager;  // Dispatcher de notificaciones (gestiona suscriptores)
        private readonly PrintJobManager _jobManager;        // Gestor de cola de impresión (para consultas de estado)

        /// <summary>
        /// Constructor — recibe dependencias inyectadas desde PrinterServicesHost.
        /// No inicia el servidor; eso se hace en Start().
        /// </summary>
        public GrpcNotificationServer(PrinterServiceDb db, NotificationManager notifManager, PrintJobManager jobManager)
        {
            _db = db;
            _notifManager = notifManager;
            _jobManager = jobManager;
        }

        /// <summary>
        /// Inicia el servidor gRPC en el puerto y dirección configurados.
        /// Lee GrpcPort (default 50051) y GrpcBindAddress (default 0.0.0.0) de ConfigManager.
        /// ServerCredentials.Insecure = sin TLS (red local de restaurante).
        /// </summary>
        public void Start()
        {
            // Leer configuración dinámica de puerto y dirección de binding
            var cfg = ConfigManager.Instance;
            int port = cfg.GetInt("GrpcPort", 50051);               // Puerto gRPC (configurable via API)
            string host = cfg.GetString("GrpcBindAddress", "0.0.0.0"); // 0.0.0.0 = escuchar en todas las interfaces

            // Crear la implementación del servicio con sus dependencias
            var serviceImpl = new PrinterNotificationServiceImpl(_db, _notifManager, _jobManager);

            // Construir y configurar el servidor gRPC
            _server = new Server
            {
                // Registrar el servicio generado por protoc (BindService enlaza los 3 RPCs)
                Services = { PrinterNotification.BindService(serviceImpl) },
                // Configurar puerto — Insecure porque es red local sin TLS
                Ports = { new ServerPort(host, port, ServerCredentials.Insecure) }
            };

            _server.Start(); // Comienza a aceptar conexiones gRPC
            Log.InfoFormat("[gRPC] Servidor gRPC iniciado en {0}:{1}", host, port);
        }

        /// <summary>
        /// Detiene el servidor gRPC de forma ordenada.
        /// Primero intenta ShutdownAsync (graceful, espera 5s para que streams terminen).
        /// Si falla, usa KillAsync (forzado) como fallback.
        /// Llamado desde PrinterServicesHost.Stop().
        /// </summary>
        public void Stop()
        {
            if (_server != null)
            {
                try
                {
                    // Shutdown graceful — espera hasta 5 segundos para cerrar streams abiertos
                    _server.ShutdownAsync().Wait(TimeSpan.FromSeconds(5));
                    Log.Info("[gRPC] Servidor gRPC detenido");
                }
                catch (Exception ex)
                {
                    Log.Warn("[gRPC] Error deteniendo servidor gRPC: " + ex.Message);
                    // Fallback: kill forzado si el shutdown graceful falló
                    try { _server.KillAsync().Wait(TimeSpan.FromSeconds(2)); }
                    catch { } // Ignorar error en kill — el proceso está terminando
                }
            }
        }
    }
}
