using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using log4net;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Notifications;
using PrinterServices.Queue;

namespace PrinterServices.Grpc
{
    /// <summary>
    /// Implementación server-side del servicio gRPC PrinterNotification.
    /// Hereda de PrinterNotificationBase (generado por protoc desde printer_notification.proto).
    /// 
    /// Provee 3 RPCs:
    ///   1. SuscribirNotificacionesServidor — server-streaming, el Servidor POS recibe TODAS las notificaciones
    ///   2. SuscribirNotificacionesCliente  — server-streaming, cada Cliente POS recibe solo las de SUS comandas
    ///   3. GetStatusPrinters               — unary, devuelve estado actual de todas las impresoras registradas
    /// 
    /// Los streams se mantienen abiertos indefinidamente hasta que el cliente se desconecte
    /// o el servidor se detenga. NotificationManager es quien hace WriteAsync() en los streams
    /// cuando llegan eventos de PrintWorker o StatusMonitor.
    /// </summary>
    public class PrinterNotificationServiceImpl : PrinterNotification.PrinterNotificationBase
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterNotificationServiceImpl));

        private readonly PrinterServiceDb _db;               // BD para queries de impresoras y jobs
        private readonly NotificationManager _notifManager;  // Gestiona suscriptores y difunde eventos
        private readonly PrintJobManager _jobManager;        // Cola de impresión (para consultas)

        /// <summary>
        /// Constructor — recibe dependencias desde GrpcNotificationServer.
        /// </summary>
        public PrinterNotificationServiceImpl(PrinterServiceDb db, NotificationManager notifManager, PrintJobManager jobManager)
        {
            _db = db;
            _notifManager = notifManager;
            _jobManager = jobManager;
        }

        /// <summary>
        /// Server-streaming: el Servidor se suscribe y recibe TODAS las notificaciones.
        /// El stream permanece abierto hasta que el cliente se desconecta.
        /// </summary>
        public override async Task SuscribirNotificacionesServidor(
            SuscripcionRequest request,
            IServerStreamWriter<NotificacionEvent> responseStream,
            ServerCallContext context)
        {
            Log.InfoFormat("[gRPC] Servidor conectado: device={0} ip={1}", request.DeviceId, request.Ip);

            // Registrar este servidor como suscriptor en NotificationManager.
            // El TCS (TaskCompletionSource) se usa como señal: cuando se completa,
            // este método sale del loop y el stream se cierra.
            var tcs = _notifManager.AgregarServidor(request.DeviceId, request.Ip, responseStream);

            // Al conectarse, enviar notificaciones que se generaron mientras el servidor estaba desconectado.
            // Esto permite entrega diferida: las notificaciones se persisten en SQLite y se envían aquí.
            await EnviarPendientes(request.DeviceId, responseStream);

            try
            {
                // Mantener el stream abierto indefinidamente.
                // Los eventos se envían vía NotificationManager.SendToStream() cuando llegan de PrintWorker/StatusMonitor.
                // El loop solo verifica periódicamente si debe cerrarse (cancelación o señal del TCS).
                while (!context.CancellationToken.IsCancellationRequested)
                {
                    if (tcs.Task.IsCompleted) break; // Señal de cierre (reconex o shutdown)
                    await Task.Delay(1000, context.CancellationToken); // Chequeo cada 1s
                }
            }
            catch (OperationCanceledException)
            {
                // Normal: el cliente cerró la conexión o el servidor se está deteniendo
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[gRPC] Error en stream servidor {0}: {1}", request.DeviceId, ex.Message);
            }
            finally
            {
                // Limpiar: remover suscriptor del diccionario al salir del método
                _notifManager.RemoverServidor(request.DeviceId);
                Log.InfoFormat("[gRPC] Servidor desconectado: {0}", request.DeviceId);
            }
        }

        /// <summary>
        /// Server-streaming: un Cliente se suscribe y recibe notificaciones de SUS comandas.
        /// Filtra por device_id_origen.
        /// </summary>
        public override async Task SuscribirNotificacionesCliente(
            SuscripcionRequest request,
            IServerStreamWriter<NotificacionEvent> responseStream,
            ServerCallContext context)
        {
            Log.InfoFormat("[gRPC] Cliente conectado: device={0} ip={1}", request.DeviceId, request.Ip);

            // Registrar este cliente como suscriptor.
            // Solo recibirá notificaciones donde device_id_origen == su deviceId
            // (es decir, solo las comandas que él mismo generó).
            var tcs = _notifManager.AgregarCliente(request.DeviceId, request.Ip, responseStream);

            // Enviar notificaciones pendientes específicas de este cliente
            await EnviarPendientes(request.DeviceId, responseStream);

            try
            {
                // Mismo patrón que SuscribirNotificacionesServidor:
                // mantener stream abierto hasta desconexión o señal de cierre.
                while (!context.CancellationToken.IsCancellationRequested)
                {
                    if (tcs.Task.IsCompleted) break; // Señal de cierre
                    await Task.Delay(1000, context.CancellationToken); // Chequeo cada 1s
                }
            }
            catch (OperationCanceledException)
            {
                // Normal: el cliente POS cerró la conexión
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[gRPC] Error en stream cliente {0}: {1}", request.DeviceId, ex.Message);
            }
            finally
            {
                // Limpiar: remover suscriptor del diccionario
                _notifManager.RemoverCliente(request.DeviceId);
                Log.InfoFormat("[gRPC] Cliente desconectado: {0}", request.DeviceId);
            }
        }

        /// <summary>
        /// Unary: devuelve estado de todas las impresoras registradas.
        /// </summary>
        public override Task<StatusPrintersResponse> GetStatusPrinters(Empty request, ServerCallContext context)
        {
            var response = new StatusPrintersResponse();

            try
            {
                // Consultar todas las impresoras registradas en la tabla 'printers'
                var printers = _db.Query<PrinterEntity>("SELECT * FROM printers");

                foreach (var p in printers)
                {
                    // Contar jobs pendientes (PENDING, PRINTING o WAITING) para esta impresora
                    // Esto permite al cliente ver cuántos trabajos tiene en cola cada impresora
                    var pendingCount = _db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM print_jobs WHERE impresora_id = ? AND estado IN ('PENDING','PRINTING','WAITING')",
                        p.ImpresoraId);

                    // Construir mensaje protobuf PrinterStatusInfo con los datos de BD
                    // Los campos int (EstadoOnline, TienePapel, TapaAbierta) se convierten a bool
                    // porque la tabla SQLite usa 0/1 pero el proto usa bool
                    response.Printers.Add(new PrinterStatusInfo
                    {
                        ImpresoraId = p.ImpresoraId ?? "",     // ID único de la impresora
                        Nombre = p.Nombre ?? "",               // Nombre legible (ej: "Cocina")
                        Ip = p.Ip ?? "",                       // IP de la impresora (ej: "10.0.0.50")
                        Online = p.EstadoOnline == 1,          // true si el último DLE EOT fue exitoso
                        TienePapel = p.TienePapel == 1,        // true si DLE EOT 4 reportó papel ok
                        TapaAbierta = p.TapaAbierta == 1,      // true si DLE EOT 2 reportó tapa abierta
                        UltimoCheck = p.UltimoCheck ?? "",     // Timestamp ISO 8601 del último chequeo
                        JobsPendientes = pendingCount           // Cantidad de jobs en cola
                    });
                }

                Log.DebugFormat("[gRPC] GetStatusPrinters: {0} impresoras", response.Printers.Count);
            }
            catch (Exception ex)
            {
                Log.Error("[gRPC] Error en GetStatusPrinters: " + ex.Message, ex);
            }

            // Task.FromResult porque este método es síncrono (no hay await)
            // pero la firma del override requiere Task<T>
            return Task.FromResult(response);
        }

        // ─── Helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Envía notificaciones pendientes almacenadas en SQLite al suscriptor recién conectado.
        /// Esto implementa "entrega diferida": si el suscriptor estuvo desconectado cuando
        /// ocurrieron eventos, los recibe al reconectarse.
        /// Después de enviarlas, las marca como entregadas (entregada = 1) para no reenviarlas.
        /// </summary>
        private async Task EnviarPendientes(string deviceId, IServerStreamWriter<NotificacionEvent> stream)
        {
            try
            {
                // Consultar notificaciones no entregadas para este device (ORDER BY fecha ASC)
                var pendientes = _notifManager.GetPendingNotifications(deviceId);
                if (pendientes.Count == 0) return; // Nada pendiente — salir

                var ids = new System.Collections.Generic.List<int>(); // IDs a marcar como entregadas
                foreach (var n in pendientes)
                {
                    // Convertir NotificationEntity (SQLite) a NotificacionEvent (protobuf)
                    var evt = new NotificacionEvent
                    {
                        Tipo = n.Tipo ?? "",
                        ComandaId = n.ComandaId ?? "",
                        ImpresoraId = n.ImpresoraId ?? "",
                        ImpresoraNombre = n.ImpresoraNombre ?? "",
                        DeviceIdOrigen = n.DeviceId ?? "",
                        Mensaje = n.Mensaje ?? "",
                        JobId = n.JobId ?? "",
                        Timestamp = n.Fecha ?? ""
                    };
                    await stream.WriteAsync(evt); // Push al suscriptor via gRPC stream
                    ids.Add(n.NotifId);           // Acumular ID para marcar como entregada
                }

                // Marcar todas como entregadas en SQLite (UPDATE entregada = 1)
                _notifManager.MarkDelivered(ids);
                Log.InfoFormat("[gRPC] Enviadas {0} notificaciones pendientes a {1}", ids.Count, deviceId);
            }
            catch (Exception ex)
            {
                // No relanzar — el stream puede continuar aunque falle el envío de pendientes
                Log.Warn("[gRPC] Error enviando pendientes a " + deviceId + ": " + ex.Message);
            }
        }
    }
}
