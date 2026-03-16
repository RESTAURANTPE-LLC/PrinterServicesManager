using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Grpc;

namespace PrinterServices.Notifications
{
    /// <summary>
    /// Constantes de tipos de notificación que el sistema puede emitir.
    /// Usadas como valor del campo "tipo" en NotificacionEvent (protobuf).
    /// Cada tipo mapea a un evento específico del flujo de impresión o monitoreo.
    /// </summary>
    public static class NotificationType
    {
        public const string Impresa = "IMPRESA";         // Job impreso exitosamente
        public const string Fallida = "FALLIDA";         // Job falló después de agotar reintentos
        public const string Offline = "OFFLINE";         // Impresora pasó de ONLINE a OFFLINE
        public const string Online = "ONLINE";           // Impresora pasó de OFFLINE a ONLINE
        public const string SinPapel = "SIN_PAPEL";      // Impresora detectada sin papel (DLE EOT 4)
        public const string TapaAbierta = "TAPA_ABIERTA"; // Impresora con tapa abierta (DLE EOT 2)
        public const string Reintento = "REINTENTO";     // Job re-encolado para un nuevo intento
        public const string Esperando = "ESPERANDO";     // Job movido a WAITING (impresora no disponible)
        public const string Expirado = "EXPIRADO";       // Fase 23: Job WAITING que superó tiempo máximo configurado
    }

    /// <summary>
    /// Representa un suscriptor gRPC (Servidor o Cliente) conectado via server-streaming.
    /// Cada instancia vive mientras el stream gRPC esté activo.
    /// Cuando se desconecta, se remueve del diccionario correspondiente.
    /// </summary>
    internal class Suscriptor
    {
        public string DeviceId { get; set; }     // Identificador único del dispositivo (ej: "POS-01")
        public string Ip { get; set; }            // IP del dispositivo conectado
        public string Rol { get; set; }           // "SERVIDOR" (recibe todo) o "CLIENTE" (solo sus comandas)
        public global::Grpc.Core.IServerStreamWriter<NotificacionEvent> Stream { get; set; } // Stream gRPC abierto para push de eventos
        public TaskCompletionSource<bool> Completion { get; set; } // Se completa cuando el stream debe cerrarse
        public DateTime ConectadoDesde { get; set; } // Timestamp de conexión (para diagnóstico)
    }

    /// <summary>
    /// Dispatcher central de notificaciones — Singleton.
    /// 
    /// Arquitectura:
    ///   PrintWorker/StatusMonitor → NotificationManager → gRPC streams (push)
    ///                                                   → SQLite (persistencia diferida)
    /// 
    /// Responsabilidades:
    /// 1. Mantener registro de suscriptores gRPC (servidores y clientes)
    /// 2. Difundir eventos de impresión (éxito, fallo, espera, reintento) a suscriptores activos
    /// 3. Difundir eventos de estado de impresora (online/offline/sin papel) a servidores
    /// 4. Persistir notificaciones en SQLite para entrega diferida cuando un suscriptor se reconecte
    /// 
    /// Thread-safety: Usa ConcurrentDictionary para acceso concurrente desde múltiples workers.
    /// </summary>
    public class NotificationManager
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NotificationManager));

        private static NotificationManager _instance;          // Instancia singleton
        private static readonly object _lock = new object();   // Lock para inicialización thread-safe

        private readonly PrinterServiceDb _db;                 // Conexión SQLite para persistir notificaciones
        private readonly ConcurrentDictionary<string, Suscriptor> _servidores; // Suscriptores rol=SERVIDOR (key=deviceId)
        private readonly ConcurrentDictionary<string, Suscriptor> _clientes;   // Suscriptores rol=CLIENTE (key=deviceId)

        /// <summary>
        /// Constructor privado — solo se instancia via GetInstance().
        /// </summary>
        private NotificationManager(PrinterServiceDb db)
        {
            _db = db;
            _servidores = new ConcurrentDictionary<string, Suscriptor>();
            _clientes = new ConcurrentDictionary<string, Suscriptor>();
        }

        /// <summary>
        /// Inicializa el singleton con la conexión a BD.
        /// Se llama una vez en PrinterServicesHost.Start() (paso 7).
        /// Double-checked locking para thread-safety.
        /// </summary>
        public static NotificationManager GetInstance(PrinterServiceDb db)
        {
            if (_instance != null) return _instance;
            lock (_lock)
            {
                if (_instance != null) return _instance;
                _instance = new NotificationManager(db);
                return _instance;
            }
        }

        /// <summary>
        /// Acceso al singleton ya inicializado.
        /// Usado por PrintWorker y StatusMonitor para emitir notificaciones.
        /// Lanza InvalidOperationException si GetInstance() no fue llamado aún.
        /// </summary>
        public static NotificationManager Instance
        {
            get
            {
                if (_instance == null)
                    throw new InvalidOperationException("NotificationManager no ha sido inicializado. Llame a GetInstance() primero.");
                return _instance;
            }
        }

        // ─── Gestión de suscriptores ────────────────────────────────────

        /// <summary>
        /// Registra un suscriptor de tipo Servidor (recibe TODAS las notificaciones).
        /// </summary>
        internal TaskCompletionSource<bool> AgregarServidor(string deviceId, string ip,
            global::Grpc.Core.IServerStreamWriter<NotificacionEvent> stream)
        {
            // TaskCompletionSource actúa como señal: cuando se completa, el stream gRPC debe cerrarse
            var tcs = new TaskCompletionSource<bool>();
            var sub = new Suscriptor
            {
                DeviceId = deviceId,
                Ip = ip,
                Rol = "SERVIDOR",
                Stream = stream,          // Stream gRPC abierto — WriteAsync() le envía eventos push
                Completion = tcs,
                ConectadoDesde = DateTime.Now
            };

            // AddOrUpdate: si ya existía una conexión anterior del mismo deviceId,
            // cerramos el stream viejo (TrySetResult) y lo reemplazamos con el nuevo
            _servidores.AddOrUpdate(deviceId, sub, (k, old) =>
            {
                old.Completion.TrySetResult(true); // señal al stream anterior para que termine
                return sub;
            });

            Log.InfoFormat("[NOTIF] Servidor suscrito: {0} ({1})", deviceId, ip);
            return tcs; // El caller (ServiceImpl) espera este TCS para mantener el stream abierto
        }

        /// <summary>
        /// Registra un suscriptor de tipo Cliente (recibe notificaciones de SUS comandas).
        /// </summary>
        internal TaskCompletionSource<bool> AgregarCliente(string deviceId, string ip,
            global::Grpc.Core.IServerStreamWriter<NotificacionEvent> stream)
        {
            var tcs = new TaskCompletionSource<bool>();
            var sub = new Suscriptor
            {
                DeviceId = deviceId,
                Ip = ip,
                Rol = "CLIENTE",
                Stream = stream,
                Completion = tcs,
                ConectadoDesde = DateTime.Now
            };

            // Mismo patrón que AgregarServidor: reemplaza conexión anterior si existe
            _clientes.AddOrUpdate(deviceId, sub, (k, old) =>
            {
                old.Completion.TrySetResult(true); // cierra stream anterior del mismo cliente
                return sub;
            });

            Log.InfoFormat("[NOTIF] Cliente suscrito: {0} ({1})", deviceId, ip);
            return tcs;
        }

        /// <summary>
        /// Remueve un servidor suscrito y señala el cierre de su stream.
        /// Llamado desde PrinterNotificationServiceImpl.finally cuando el stream termina.
        /// </summary>
        internal void RemoverServidor(string deviceId)
        {
            Suscriptor removed;
            if (_servidores.TryRemove(deviceId, out removed))
            {
                removed.Completion.TrySetResult(true); // Señal de cierre al loop del stream
                Log.InfoFormat("[NOTIF] Servidor desconectado: {0}", deviceId);
            }
        }

        /// <summary>
        /// Remueve un cliente suscrito y señala el cierre de su stream.
        /// Llamado desde PrinterNotificationServiceImpl.finally cuando el stream termina.
        /// </summary>
        internal void RemoverCliente(string deviceId)
        {
            Suscriptor removed;
            if (_clientes.TryRemove(deviceId, out removed))
            {
                removed.Completion.TrySetResult(true); // Señal de cierre al loop del stream
                Log.InfoFormat("[NOTIF] Cliente desconectado: {0}", deviceId);
            }
        }

        /// <summary>Cantidad de servidores actualmente conectados via gRPC.</summary>
        public int ServidoresConectados { get { return _servidores.Count; } }
        /// <summary>Cantidad de clientes actualmente conectados via gRPC.</summary>
        public int ClientesConectados { get { return _clientes.Count; } }

        // ─── Emisión de notificaciones ──────────────────────────────────

        /// <summary>
        /// Notifica que un job se imprimió exitosamente.
        /// Va a: todos los servidores + el cliente que originó la comanda.
        /// </summary>
        public void NotifyPrintSuccess(string jobId, string comandaId, string impresoraId,
            string impresoraNombre, string deviceIdOrigen, string ipOrigen)
        {
            // Construir evento protobuf con tipo IMPRESA y timestamp actual
            var evt = CreateEvent(NotificationType.Impresa, jobId, comandaId,
                impresoraId, impresoraNombre, deviceIdOrigen, ipOrigen,
                "Comanda impresa exitosamente en " + (impresoraNombre ?? impresoraId), 0);

            BroadcastToServidores(evt);       // Push a todos los servidores suscritos
            SendToCliente(deviceIdOrigen, evt); // Push al cliente que originó la comanda
            PersistNotification(evt);          // Guardar en SQLite para entrega diferida
        }

        /// <summary>
        /// Notifica que un job falló después de agotar reintentos.
        /// </summary>
        public void NotifyPrintFailed(string jobId, string comandaId, string impresoraId,
            string impresoraNombre, string deviceIdOrigen, string ipOrigen,
            string error, int reintentos)
        {
            // Construir evento protobuf con tipo FALLIDA, incluye mensaje de error y conteo de reintentos
            var evt = CreateEvent(NotificationType.Fallida, jobId, comandaId,
                impresoraId, impresoraNombre, deviceIdOrigen, ipOrigen,
                "Impresión fallida: " + error, reintentos);

            BroadcastToServidores(evt);       // El servidor necesita saber que falló para mostrar alerta
            SendToCliente(deviceIdOrigen, evt); // El cliente que originó la comanda debe actualizar su UI
            PersistNotification(evt);          // Persistir para entrega diferida si no están conectados
        }

        /// <summary>
        /// Notifica que un job está en espera (impresora offline/sin papel).
        /// </summary>
        public void NotifyPrintWaiting(string jobId, string comandaId, string impresoraId,
            string impresoraNombre, string deviceIdOrigen, string ipOrigen, string reason)
        {
            // Job está esperando — la impresora no está disponible (offline o sin papel)
            // El job permanece en estado WAITING hasta que StatusMonitor detecte que la impresora volvió
            var evt = CreateEvent(NotificationType.Esperando, jobId, comandaId,
                impresoraId, impresoraNombre, deviceIdOrigen, ipOrigen,
                reason, 0);

            BroadcastToServidores(evt);       // Servidor muestra que hay jobs en espera
            SendToCliente(deviceIdOrigen, evt); // Cliente sabe que su comanda está pendiente
            PersistNotification(evt);
        }

        /// <summary>
        /// Fase 23: Notifica que un job expiró (superó tiempo máximo en WAITING).
        /// Es un estado terminal — el job no se reintenta.
        /// Va a: todos los servidores + el cliente que originó la comanda.
        /// </summary>
        public void NotifyPrintExpired(string jobId, string comandaId, string impresoraId,
            string impresoraNombre, string deviceIdOrigen, string ipOrigen, string reason)
        {
            // Job expirado — superó el tiempo máximo configurado en estado WAITING
            var evt = CreateEvent(NotificationType.Expirado, jobId, comandaId,
                impresoraId, impresoraNombre, deviceIdOrigen, ipOrigen,
                reason, 0);

            BroadcastToServidores(evt);         // Servidor necesita saber que el job ya no se procesará
            SendToCliente(deviceIdOrigen, evt); // Cliente debe mostrar que su comanda expiró
            PersistNotification(evt);           // Persistir para entrega diferida
        }

        /// <summary>
        /// Notifica cambio de estado de impresora (online/offline/sin papel).
        /// Va solo a servidores (no tiene comanda/cliente asociado).
        /// </summary>
        public void NotifyPrinterStatusChange(string impresoraId, string impresoraNombre,
            string tipo, string mensaje)
        {
            // Evento de cambio de estado de impresora — NO tiene comanda ni cliente asociado
            // Originado por StatusMonitor cuando detecta transiciones ONLINE↔OFFLINE o SIN_PAPEL
            var evt = CreateEvent(tipo, "", "",
                impresoraId, impresoraNombre, "", "",
                mensaje, 0);

            BroadcastToServidores(evt);  // Solo va a servidores — los clientes no necesitan esto
            PersistNotification(evt);
        }

        /// <summary>
        /// Notifica un reintento de impresión.
        /// </summary>
        public void NotifyPrintRetry(string jobId, string comandaId, string impresoraId,
            string impresoraNombre, string deviceIdOrigen, string ipOrigen, int reintentos)
        {
            // Reintento: el job falló un envío TCP pero aún tiene reintentos disponibles
            // Solo se notifica a servidores (informativo) — NO se persiste ni se envía al cliente
            // porque el job aún no terminó, va a reintentar
            var evt = CreateEvent(NotificationType.Reintento, jobId, comandaId,
                impresoraId, impresoraNombre, deviceIdOrigen, ipOrigen,
                string.Format("Reintento {0}", reintentos), reintentos);

            BroadcastToServidores(evt); // Solo broadcast informativo, sin persistencia
        }

        // ─── Métodos internos ───────────────────────────────────────────

        /// <summary>
        /// Crea un NotificacionEvent (mensaje protobuf) con todos los campos.
        /// Usa null-coalescing a "" porque protobuf no permite nulls en strings.
        /// Timestamp en formato ISO 8601 ("o") para interoperabilidad.
        /// </summary>
        private NotificacionEvent CreateEvent(string tipo, string jobId, string comandaId,
            string impresoraId, string impresoraNombre, string deviceIdOrigen,
            string ipOrigen, string mensaje, int reintentos)
        {
            return new NotificacionEvent
            {
                Tipo = tipo ?? "",                       // IMPRESA, FALLIDA, OFFLINE, etc.
                JobId = jobId ?? "",                     // ID único del print job
                ComandaId = comandaId ?? "",             // ID de la comanda original
                ImpresoraId = impresoraId ?? "",         // ID de la impresora destino
                ImpresoraNombre = impresoraNombre ?? "", // Nombre legible de la impresora
                DeviceIdOrigen = deviceIdOrigen ?? "",   // Device que generó el pedido
                IpOrigen = ipOrigen ?? "",               // IP del device origen
                Mensaje = mensaje ?? "",                 // Mensaje descriptivo del evento
                Timestamp = DateTime.Now.ToString("o"),  // ISO 8601: 2026-02-11T13:37:00-05:00
                Reintentos = reintentos                  // Número de reintentos realizados
            };
        }

        /// <summary>
        /// Envía el evento a TODOS los servidores suscritos.
        /// ToArray() crea snapshot para evitar modificación durante iteración.
        /// </summary>
        private void BroadcastToServidores(NotificacionEvent evt)
        {
            foreach (var kvp in _servidores.ToArray())
            {
                SendToStream(kvp.Key, kvp.Value, evt, _servidores);
            }
        }

        /// <summary>
        /// Envía el evento a UN cliente específico (el que originó la comanda).
        /// Si el cliente no está conectado, no hace nada — la notificación ya fue persistida.
        /// </summary>
        private void SendToCliente(string deviceId, NotificacionEvent evt)
        {
            if (string.IsNullOrEmpty(deviceId)) return; // Eventos de impresora no tienen cliente

            Suscriptor sub;
            if (_clientes.TryGetValue(deviceId, out sub))
            {
                SendToStream(deviceId, sub, evt, _clientes);
            }
        }

        /// <summary>
        /// Escribe el evento en el stream gRPC del suscriptor de forma asíncrona (fire-and-forget).
        /// Si el WriteAsync falla (stream roto, cliente desconectado), remueve al suscriptor
        /// automáticamente del diccionario y señala el cierre del stream.
        /// </summary>
        private void SendToStream(string key, Suscriptor sub, NotificacionEvent evt,
            ConcurrentDictionary<string, Suscriptor> dict)
        {
            // Task.Run para no bloquear al caller (PrintWorker/StatusMonitor)
            Task.Run(async () =>
            {
                try
                {
                    await sub.Stream.WriteAsync(evt); // Push al cliente gRPC
                }
                catch (Exception ex)
                {
                    // Stream roto — el suscriptor se desconectó o hubo error de red
                    Log.WarnFormat("[NOTIF] Error enviando a {0} ({1}): {2}. Removiendo suscriptor.",
                        sub.DeviceId, sub.Rol, ex.Message);
                    Suscriptor removed;
                    dict.TryRemove(key, out removed);  // Remover del diccionario
                    if (removed != null) removed.Completion.TrySetResult(true); // Señal de cierre
                }
            });
        }

        /// <summary>
        /// Persiste la notificación en la tabla 'notifications' de SQLite.
        /// Esto permite entrega diferida: cuando un suscriptor se reconecte,
        /// recibirá las notificaciones que se generaron mientras estuvo desconectado.
        /// Campo 'entregada' = 0 (pendiente) hasta que se envíe por stream y se marque = 1.
        /// </summary>
        private void PersistNotification(NotificacionEvent evt)
        {
            try
            {
                var entity = new NotificationEntity
                {
                    // Si no hay device origen (ej: evento de impresora), usar "SYSTEM"
                    DeviceId = !string.IsNullOrEmpty(evt.DeviceIdOrigen) ? evt.DeviceIdOrigen : "SYSTEM",
                    Tipo = evt.Tipo,
                    ComandaId = evt.ComandaId,
                    JobId = evt.JobId,
                    ImpresoraId = evt.ImpresoraId,
                    ImpresoraNombre = evt.ImpresoraNombre,
                    Mensaje = evt.Mensaje,
                    Entregada = 0,    // 0 = pendiente de entrega, 1 = ya entregada
                    Fecha = evt.Timestamp
                };
                _db.Insert(entity); // INSERT en tabla notifications
            }
            catch (Exception ex)
            {
                Log.Error("[NOTIF] Error persistiendo notificación: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Obtiene notificaciones pendientes (no entregadas) para un device.
        /// </summary>
        public List<NotificationEntity> GetPendingNotifications(string deviceId)
        {
            try
            {
                // Buscar notificaciones no entregadas para este device, ordenadas por fecha
                // Se usan cuando un suscriptor se reconecta para enviar lo que se perdió
                return _db.Query<NotificationEntity>(
                    "SELECT * FROM notifications WHERE device_id = ? AND entregada = 0 ORDER BY fecha ASC",
                    deviceId);
            }
            catch (Exception ex)
            {
                Log.Error("[NOTIF] Error obteniendo pendientes: " + ex.Message, ex);
                return new List<NotificationEntity>(); // Lista vacía en caso de error
            }
        }

        /// <summary>
        /// Marca notificaciones como entregadas.
        /// </summary>
        public void MarkDelivered(IEnumerable<int> notifIds)
        {
            try
            {
                // Marcar cada notificación como entregada (entregada = 1)
                // para que no se reenvíe en una futura reconexión
                foreach (var id in notifIds)
                {
                    _db.Execute("UPDATE notifications SET entregada = 1 WHERE notif_id = ?", id);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[NOTIF] Error marcando entregadas: " + ex.Message, ex);
            }
        }
    }
}
