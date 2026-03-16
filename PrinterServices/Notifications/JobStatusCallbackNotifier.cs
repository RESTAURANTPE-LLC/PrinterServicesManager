using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using log4net;
using Newtonsoft.Json;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Queue;

namespace PrinterServices.Notifications
{
    /// <summary>
    /// Notificador que envía cambios de estado de jobs de impresión a QuipuNetX via HTTP POST.
    /// Si QuipuNetX está offline, persiste el callback en BD para reintentos posteriores.
    /// Mismo patrón que QuipuNetXNotifier (HTTP + persist + retry).
    /// 
    /// Responsabilidad ÚNICA: enviar HTTP y persistir fallidos. NO modifica estados de jobs.
    /// </summary>
    public class JobStatusCallbackNotifier
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(JobStatusCallbackNotifier)); // Logger de la clase

        private readonly PrinterServiceDb _db;     // Referencia a BD para persistir callbacks fallidos
        private readonly ConfigManager _config;     // Referencia a configuración para obtener timeouts y max retries
        private readonly HttpClient _httpClient;    // Cliente HTTP reutilizable para enviar callbacks

        /// <summary>
        /// Constructor que inicializa dependencias y configura timeout HTTP.
        /// </summary>
        /// <param name="db">Base de datos SQLite de PrinterServices</param>
        /// <param name="config">Configuración global</param>
        public JobStatusCallbackNotifier(PrinterServiceDb db, ConfigManager config)
        {
            _db = db;           // Guardar referencia a BD
            _config = config;   // Guardar referencia a config

            _httpClient = new HttpClient(); // Crear cliente HTTP reutilizable
            int timeoutMs = _config.GetInt("QuipuNetXTimeoutMs", 5000); // Obtener timeout configurado (default 5s)
            _httpClient.Timeout = TimeSpan.FromMilliseconds(timeoutMs); // Aplicar timeout al cliente
        }

        /// <summary>
        /// Notifica a QuipuNetX que un job cambió de estado.
        /// Si el envío falla, persiste en BD para retry automático.
        /// Se llama desde PrintWorker después de cada cambio de estado.
        /// </summary>
        /// <param name="job">Job con datos completos (IpOrigen, PedidoIds, etc.)</param>
        /// <param name="newStatus">Nuevo estado: DONE, FAILED, WAITING, PRINTING, EXPIRED, CANCELLED</param>
        /// <param name="error">Mensaje de error (solo para FAILED/WAITING)</param>
        public async Task NotifyStatusChangeAsync(PrintJob job, string newStatus, string error = "")
        {
            if (job == null) return;                                    // Validar que el job no sea null

            // Fase 9 Fix: Si IpServidor está vacío, usar 127.0.0.1 como fallback.
            // RAZÓN: PrinterServices SIEMPRE corre en la misma máquina que QuipuNet Servidor.
            // Si el JSON original no trajo ip_servidor (Util.getIpServerPermanente() retornó vacío),
            // el servidor está en localhost. Sin este fallback, el paso 1 se salta y la BD nunca se actualiza.
            string ipServidor = !string.IsNullOrEmpty(job.IpServidor) ? job.IpServidor : "127.0.0.1";

            Log.InfoFormat("[JOB-CALLBACK] NOTIFY: job={0} status={1} IpServidor=[{2}] IpOrigen=[{3}]",
                job.JobId, newStatus, ipServidor, job.IpOrigen ?? "NULL");

            // 1. SIEMPRE notificar al servidor (ip_servidor o localhost)
            // RAZÓN: El servidor mantiene PrintJobStatusManager global y necesita saber
            // el estado de TODOS los jobs, sin importar quién los originó.
            // SIEMPRE se ejecuta — nunca se salta porque ipServidor tiene fallback a 127.0.0.1
            if (!string.IsNullOrEmpty(ipServidor))                      // Siempre true por el fallback
            {
                try
                {
                    var dto = BuildDto(job, newStatus, error);          // Construir DTO con datos del job
                    bool sent = await TrySendCallbackAsync(ipServidor, dto, false); // Enviar HTTP al servidor (:8081)

                    if (sent)                                           // Si se envió exitosamente
                    {
                        Log.InfoFormat("[JOB-CALLBACK] ✅ Callback SERVIDOR enviado: job={0} status={1} → {2}",
                            job.JobId, newStatus, ipServidor);           // Log de éxito
                        PersistSentCallbackForIp(job, newStatus, error, ipServidor, false); // Persistir historial (ENVIADO)
                    }
                    else                                                // Si falló el envío
                    {
                        PersistFailedCallbackForIp(job, newStatus, error,
                            "Servidor QuipuNetX offline o timeout", ipServidor, false); // Persistir para retry (servidor)
                    }
                }
                catch (Exception ex)                                    // Capturar cualquier error
                {
                    Log.Error("[JOB-CALLBACK] Error notificando al SERVIDOR", ex); // Log de error
                    PersistFailedCallbackForIp(job, newStatus, error, ex.Message, ipServidor, false); // Persistir (servidor)
                }
            }

            // 2. Notificar al cliente de origen SOLO si es diferente al servidor
            // RAZÓN: El cliente que originó la comanda necesita saber si SU impresión
            // fue exitosa o falló, para mostrar badges/modales en su UI local.
            if (!string.IsNullOrEmpty(job.IpOrigen)                     // Verificar que tenga IP de origen
                && !IsLoopbackOrSameAsServer(job.IpOrigen, ipServidor))    // Solo si es diferente al servidor (usa ipServidor con fallback)
            {
                try
                {
                    var dto = BuildDto(job, newStatus, error);          // Construir DTO con datos del job
                    bool sent = await TrySendCallbackAsync(job.IpOrigen, dto, true); // Fase 9: Enviar HTTP al cliente (:8083)

                    if (sent)                                           // Si se envió exitosamente
                    {
                        Log.InfoFormat("[JOB-CALLBACK] ✅ Callback CLIENTE enviado: job={0} status={1} → {2}",
                            job.JobId, newStatus, job.IpOrigen);        // Log de éxito
                        PersistSentCallbackForIp(job, newStatus, error, job.IpOrigen, true); // Persistir historial (ENVIADO)
                    }
                    else                                                // Si falló el envío
                    {
                        PersistFailedCallbackForIp(job, newStatus, error,
                            "Cliente QuipuNetX offline o timeout", job.IpOrigen, true); // Fase 9: Persistir para retry (cliente :8083)
                    }
                }
                catch (Exception ex)                                    // Capturar cualquier error
                {
                    Log.Error("[JOB-CALLBACK] Error notificando al CLIENTE " + job.IpOrigen, ex); // Log de error
                    PersistFailedCallbackForIp(job, newStatus, error, ex.Message, job.IpOrigen, true); // Fase 9: Persistir (cliente :8083)
                }
            }

            // 3. Fallback: si no hay ip_servidor pero sí ip_origen, notificar solo a ip_origen
            // RAZÓN: Backward compatible con versiones anteriores que no envían ip_servidor
            // Fase 9 Fix: El fallback a 127.0.0.1 hace que ipServidor NUNCA sea vacío.
            // Este bloque ya no se ejecutará, pero se mantiene por backward compatibility.
            if (false && string.IsNullOrEmpty(ipServidor) && !string.IsNullOrEmpty(job.IpOrigen))
            {
                try
                {
                    var dto = BuildDto(job, newStatus, error);          // Construir DTO con datos del job
                    bool sent = await TrySendCallbackAsync(job.IpOrigen, dto, false); // Fallback: enviar a ip_origen como servidor (:8081)

                    if (sent)                                           // Si se envió exitosamente
                    {
                        Log.InfoFormat("[JOB-CALLBACK] ✅ Callback (fallback) enviado: job={0} status={1} → {2}",
                            job.JobId, newStatus, job.IpOrigen);        // Log de éxito
                    }
                    else                                                // Si falló el envío
                    {
                        PersistFailedCallback(job, newStatus, error, "QuipuNetX offline o timeout"); // Persistir
                    }
                }
                catch (Exception ex)                                    // Capturar cualquier error
                {
                    Log.Error("[JOB-CALLBACK] Error en callback fallback", ex); // Log de error
                    PersistFailedCallback(job, newStatus, error, ex.Message); // Persistir para retry
                }
            }
        }

        /// <summary>
        /// Verifica si una IP es loopback (127.0.0.1, ::1) o la misma que el servidor.
        /// Se usa para evitar enviar doble callback cuando el origen es el propio servidor.
        /// </summary>
        /// <param name="ipOrigen">IP del origen del job</param>
        /// <param name="ipServidor">IP del servidor QuipuNet</param>
        /// <returns>true si es loopback o misma IP del servidor</returns>
        private bool IsLoopbackOrSameAsServer(string ipOrigen, string ipServidor)
        {
            if (string.IsNullOrEmpty(ipOrigen)) return true;            // Sin IP origen, no enviar

            // Fase 9 Fix: Normalizar IPv6-mapped IPv4 antes de comparar
            // Nancy puede enviar IPs como "::ffff:10.0.0.2" — sin strip, la comparación falla
            string normalizedOrigen = ipOrigen.StartsWith("::ffff:")    // Verificar prefijo IPv6-mapped
                ? ipOrigen.Substring(7) : ipOrigen;                     // Strip a IPv4 puro
            string normalizedServidor = !string.IsNullOrEmpty(ipServidor) && ipServidor.StartsWith("::ffff:")
                ? ipServidor.Substring(7) : ipServidor;                 // Strip a IPv4 puro

            if (normalizedOrigen == "127.0.0.1") return true;           // Loopback IPv4
            if (normalizedOrigen == "::1") return true;                 // Loopback IPv6
            if (normalizedOrigen == "localhost") return true;           // Loopback nombre
            if (!string.IsNullOrEmpty(normalizedServidor)               // Si hay IP del servidor
                && normalizedOrigen == normalizedServidor) return true; // Misma IP = es el servidor
            return false;                                               // IP diferente = es un cliente
        }

        /// <summary>
        /// Persiste un callback EXITOSO en BD como historial.
        /// RAZÓN: Dashboard necesita ver TODOS los callbacks (enviados y fallidos) para monitoreo completo.
        /// A diferencia de PersistFailedCallbackForIp, este SIEMPRE crea un registro nuevo (no hace upsert)
        /// porque cada envío exitoso es un evento distinto que debe quedar en el historial.
        /// </summary>
        private void PersistSentCallbackForIp(PrintJob job, string newStatus, string error,
            string targetIp, bool isClient = false)
        {
            try
            {
                string pedidoIdsCsv = job.PedidoIds != null              // Convertir lista a CSV
                    ? string.Join(",", job.PedidoIds) : "";

                var callback = new JobStatusCallbackEntity               // Crear registro de historial
                {
                    JobId = job.JobId,                                   // ID del job
                    Status = newStatus,                                  // Estado notificado
                    PedidoIds = pedidoIdsCsv,                            // Pedidos como CSV
                    IpOrigen = targetIp,                                 // IP destino
                    Error = error,                                       // Error del job (puede ser vacío para DONE)
                    EstadoEnvio = "ENVIADO",                             // Enviado exitosamente
                    FechaCreacion = DateTime.Now,                        // Timestamp de creación
                    FechaEnvio = DateTime.Now,                           // Timestamp de envío exitoso
                    Intentos = 1,                                        // Se envió al primer intento
                    UltimoError = null,                                  // Sin errores
                    EsCliente = isClient ? 1 : 0                         // 1=cliente(:8083), 0=servidor(:8081)
                };

                _db.Insert(callback);                                    // Insertar en BD
            }
            catch (Exception ex)
            {
                // No es crítico — solo historial. No debe afectar el flujo principal.
                Log.Warn("[JOB-CALLBACK] No se pudo persistir historial de callback enviado: " + ex.Message);
            }
        }

        /// <summary>
        /// Persiste un callback fallido en BD para retry, usando un IP destino específico.
        /// Variante de PersistFailedCallback que acepta IP destino explícito (para doble notificación).
        /// </summary>
        /// <summary>
        /// Fase 9 Fix: Parámetro isClient para que RetryCallbackAsync sepa si debe usar :8083 o :8081 al reintentar.
        /// </summary>
        private void PersistFailedCallbackForIp(PrintJob job, string newStatus, string error,
            string sendError, string targetIp, bool isClient = false)
        {
            try
            {
                var existing = _db.Query<JobStatusCallbackEntity>(       // Buscar callback pendiente existente
                    "SELECT * FROM job_status_callbacks WHERE job_id = ? AND ip_origen = ? AND estado_envio = 'PENDIENTE'",
                    job.JobId, targetIp).FirstOrDefault();               // Filtrar por job_id + IP destino

                string pedidoIdsCsv = job.PedidoIds != null              // Convertir lista a CSV
                    ? string.Join(",", job.PedidoIds) : "";

                if (existing != null)                                    // Si ya existe callback pendiente
                {
                    existing.Status = newStatus;                         // Actualizar con estado más reciente
                    existing.Error = error;                              // Actualizar error
                    existing.UltimoError = sendError;                    // Guardar error de envío
                    existing.EsCliente = isClient ? 1 : 0;              // Fase 9: Actualizar flag cliente/servidor
                    _db.Update(existing);                                // Persistir actualización

                    Log.InfoFormat("[JOB-CALLBACK] 📝 Callback PENDIENTE actualizado: job={0} status={1} → {2} (esCliente={3})",
                        job.JobId, newStatus, targetIp, isClient);       // Log de actualización
                }
                else                                                     // Si no existe, crear nuevo
                {
                    var newCallback = new JobStatusCallbackEntity        // Crear nueva entidad
                    {
                        JobId = job.JobId,                               // ID del job
                        Status = newStatus,                              // Estado a notificar
                        PedidoIds = pedidoIdsCsv,                        // Pedidos como CSV
                        IpOrigen = targetIp,                             // IP destino (servidor O cliente)
                        Error = error,                                   // Error del job
                        EstadoEnvio = "PENDIENTE",                       // Estado de envío
                        FechaCreacion = DateTime.Now,                    // Timestamp
                        FechaEnvio = null,                               // Aún no enviado
                        Intentos = 0,                                    // Sin intentos
                        UltimoError = sendError,                         // Error del primer intento
                        EsCliente = isClient ? 1 : 0                    // Fase 9: 1=cliente(:8083), 0=servidor(:8081)
                    };

                    _db.Insert(newCallback);                             // Insertar en BD

                    Log.InfoFormat("[JOB-CALLBACK] 📝 Callback persistido: job={0} status={1} → {2} (esCliente={3})",
                        job.JobId, newStatus, targetIp, isClient);       // Log de persistencia
                }
            }
            catch (Exception ex)
            {
                Log.Error("[JOB-CALLBACK] ❌ ERROR CRÍTICO: No se pudo persistir callback para " + targetIp, ex);
            }
        }

        /// <summary>
        /// Notifica de forma fire-and-forget (no bloquea al PrintWorker).
        /// Encapsula NotifyStatusChangeAsync en Task.Run para que el worker no espere.
        /// </summary>
        public void NotifyStatusChangeFireAndForget(PrintJob job, string newStatus, string error = "")
        {
            Task.Run(async () =>                                       // Ejecutar en background sin bloquear
            {
                try
                {
                    await NotifyStatusChangeAsync(job, newStatus, error); // Delegar al método async
                }
                catch (Exception ex)
                {
                    Log.Error("[JOB-CALLBACK] Error en fire-and-forget: " + ex.Message); // Log pero no relanzar
                }
            });
        }

        /// <summary>
        /// Reintenta enviar un callback persistido en BD.
        /// Llamado por NotificationRetryWorker en cada ciclo de retry.
        /// Mismo patrón que QuipuNetXNotifier.RetryNotificationAsync().
        /// </summary>
        /// <param name="callback">Entidad de callback pendiente de BD</param>
        /// <returns>true si se envió exitosamente</returns>
        public async Task<bool> RetryCallbackAsync(JobStatusCallbackEntity callback)
        {
            try
            {
                var pedidoIds = ParsePedidoIds(callback.PedidoIds);    // Convertir "1772,1773" → List<string>
                var dto = new JobStatusCallbackDto(                     // Construir DTO desde entidad BD
                    callback.JobId, callback.Status, pedidoIds, callback.Error);

                Log.InfoFormat("[JOB-CALLBACK] Reintentando callback #{0}: job={1} status={2} (intento {3})",
                    callback.Id, callback.JobId, callback.Status, callback.Intentos + 1); // Log de reintento

                // Fase 9 Fix: Usar EsCliente para determinar puerto y endpoint correcto al reintentar
                // Sin esto, todos los retries iban a :8081 — los clientes escuchan en :8083
                bool isClient = callback.EsCliente == 1;               // 1 = cliente (:8083), 0 = servidor (:8081)
                bool sent = await TrySendCallbackAsync(callback.IpOrigen, dto, isClient); // Intentar envío HTTP con puerto correcto

                callback.Intentos++;                                   // Incrementar contador de intentos

                if (sent)
                {
                    callback.EstadoEnvio = "ENVIADO";                  // Marcar como enviado exitosamente
                    callback.FechaEnvio = DateTime.Now;                // Registrar timestamp de envío
                    callback.UltimoError = null;                       // Limpiar error
                    _db.Update(callback);                              // Persistir cambio en BD

                    Log.InfoFormat("[JOB-CALLBACK] ✅ Callback #{0} enviado (tras {1} intentos)",
                        callback.Id, callback.Intentos);               // Log de éxito
                    return true;
                }
                else
                {
                    return HandleRetryFailure(callback);               // Manejar fallo de reintento
                }
            }
            catch (Exception ex)
            {
                Log.Error("[JOB-CALLBACK] Error al reintentar callback #" + callback.Id, ex); // Log de error

                callback.Intentos++;                                   // Incrementar contador
                callback.UltimoError = ex.Message;                     // Guardar error
                _db.Update(callback);                                  // Persistir

                return false;
            }
        }

        // ─── Métodos privados (cada uno con responsabilidad única) ───────────

        /// <summary>
        /// Construye el DTO a partir de los datos del job.
        /// </summary>
        private JobStatusCallbackDto BuildDto(PrintJob job, string newStatus, string error)
        {
            return new JobStatusCallbackDto(                           // Crear DTO con datos del job
                job.JobId,                                             // ID del job
                newStatus,                                             // Nuevo estado
                job.PedidoIds ?? new List<string>(),                   // Lista de pedidos (vacía si null)
                error ?? "",                                           // Error (vacío si null)
                job.ImpresoraNombre ?? "",                             // Nombre de impresora para UI
                job.AreaImpresion ?? "");                              // Área de producción para UI
        }

        /// <summary>
        /// Intenta enviar el callback HTTP POST a QuipuNetX.
        /// 
        /// Fase 9: Distingue entre SERVIDOR y CLIENTE para usar puerto y endpoint correctos:
        /// - SERVIDOR (:8081): POST /api/rest/printerservice/updateJobStatus (WebServer_New completo)
        /// - CLIENTE  (:8083): POST /api/ps/callback (Mini Nancy server, solo RAM, sin BD)
        /// 
        /// El puerto 8083 está hardcodeado para todos los clientes.
        /// </summary>
        /// <param name="ipDestino">IP del destino (servidor o cliente)</param>
        /// <param name="dto">DTO con datos del callback</param>
        /// <param name="isClient">true si el destino es un cliente (puerto 8083), false si es el servidor (puerto 8081)</param>
        private async Task<bool> TrySendCallbackAsync(string ipDestino, JobStatusCallbackDto dto, bool isClient = false)
        {
            try
            {
                // Fase 9: Seleccionar puerto y ruta según destino
                // SERVIDOR: puerto 8081 (WebServer_New) + endpoint completo de PrinterServiceModule
                // CLIENTE:  puerto 8083 (PrintJobCallbackServer) + endpoint del mini server
                string port;                                                            // Puerto HTTP del destino
                string path;                                                            // Ruta del endpoint
                if (isClient)
                {
                    port = "8083";                                                      // Puerto fijo del mini Nancy (hardcodeado)
                    path = "/api/ps/callback";                                          // Endpoint del PrintJobCallbackModule
                }
                else
                {
                    port = _config.GetString("QuipuNetXPort", "8081");                  // Puerto HTTP de QuipuNetX servidor
                    path = "/api/rest/printerservice/updateJobStatus";                  // Endpoint del PrinterServiceModule
                }

                string endpoint = string.Format("http://{0}:{1}{2}",
                    ipDestino, port, path);                                             // Construir URL completa

                string jsonBody = JsonConvert.SerializeObject(dto);                     // Serializar DTO a JSON
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json"); // Crear contenido HTTP

                Log.DebugFormat("[JOB-CALLBACK] POST {0} | Body: {1}", endpoint, jsonBody); // Log del request

                HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content); // Enviar POST

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();   // Leer respuesta
                    Log.DebugFormat("[JOB-CALLBACK] Respuesta OK: {0}", responseBody);  // Log de respuesta
                    return true;                                                        // Éxito
                }
                else
                {
                    string errorBody = await response.Content.ReadAsStringAsync();      // Leer error
                    Log.WarnFormat("[JOB-CALLBACK] QuipuNetX respondió {0}: {1}",
                        (int)response.StatusCode, errorBody);                            // Log de error HTTP
                    return false;                                                        // Fallo
                }
            }
            catch (TaskCanceledException)
            {
                Log.Warn("[JOB-CALLBACK] Timeout al conectar con QuipuNetX");            // Timeout
                return false;
            }
            catch (HttpRequestException ex)
            {
                Log.WarnFormat("[JOB-CALLBACK] Error de conexión con QuipuNetX: {0}", ex.Message); // Sin conexión
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("[JOB-CALLBACK] Error al enviar HTTP request", ex);            // Error inesperado
                return false;
            }
        }

        /// <summary>
        /// Persiste un callback fallido en BD para retry automático.
        /// Si ya existe un callback PENDIENTE para el mismo job_id, lo actualiza (idempotente).
        /// </summary>
        private void PersistFailedCallback(PrintJob job, string newStatus, string error, string sendError)
        {
            try
            {
                var existing = _db.Query<JobStatusCallbackEntity>(             // Buscar callback pendiente existente
                    "SELECT * FROM job_status_callbacks WHERE job_id = ? AND estado_envio = 'PENDIENTE'",
                    job.JobId).FirstOrDefault();

                string pedidoIdsCsv = job.PedidoIds != null                    // Convertir lista a CSV
                    ? string.Join(",", job.PedidoIds) : "";

                if (existing != null)
                {
                    existing.Status = newStatus;                               // Actualizar con estado más reciente
                    existing.Error = error;                                     // Actualizar error
                    existing.UltimoError = sendError;                          // Guardar error de envío
                    _db.Update(existing);                                      // Persistir actualización

                    Log.InfoFormat("[JOB-CALLBACK] 📝 Callback PENDIENTE actualizado: job={0} status={1}",
                        job.JobId, newStatus);                                 // Log de actualización
                }
                else
                {
                    var newCallback = new JobStatusCallbackEntity              // Crear nueva entidad
                    {
                        JobId = job.JobId,                                     // ID del job
                        Status = newStatus,                                    // Estado a notificar
                        PedidoIds = pedidoIdsCsv,                              // Pedidos como CSV
                        IpOrigen = job.IpOrigen,                               // IP destino
                        Error = error,                                         // Error del job
                        EstadoEnvio = "PENDIENTE",                             // Estado de envío
                        FechaCreacion = DateTime.Now,                          // Timestamp
                        FechaEnvio = null,                                     // Aún no enviado
                        Intentos = 0,                                          // Sin intentos
                        UltimoError = sendError                                // Error del primer intento
                    };

                    _db.Insert(newCallback);                                   // Insertar en BD

                    Log.InfoFormat("[JOB-CALLBACK] 📝 Callback persistido: job={0} status={1} → {2}",
                        job.JobId, newStatus, job.IpOrigen);                   // Log de persistencia
                }
            }
            catch (Exception ex)
            {
                Log.Error("[JOB-CALLBACK] ❌ ERROR CRÍTICO: No se pudo persistir callback en BD", ex); // Error fatal
            }
        }

        /// <summary>
        /// Maneja el fallo de un reintento: marca como FALLIDO si superó max retries.
        /// </summary>
        private bool HandleRetryFailure(JobStatusCallbackEntity callback)
        {
            int maxRetries = _config.GetInt("NotificationMaxRetries", 10);     // Obtener max retries de config

            if (callback.Intentos >= maxRetries)
            {
                callback.EstadoEnvio = "FALLIDO";                             // Marcar como fallido definitivo
                callback.UltimoError = string.Format(
                    "Máximo de {0} reintentos alcanzado", maxRetries);         // Mensaje de max retries
                _db.Update(callback);                                          // Persistir

                Log.ErrorFormat("[JOB-CALLBACK] ❌ Callback #{0} marcado como FALLIDO (tras {1} intentos)",
                    callback.Id, callback.Intentos);                           // Log de fallo definitivo
            }
            else
            {
                callback.UltimoError = "QuipuNetX offline o timeout";          // Error temporal
                _db.Update(callback);                                          // Persistir

                Log.WarnFormat("[JOB-CALLBACK] ⏳ Callback #{0} sigue PENDIENTE (intento {1}/{2})",
                    callback.Id, callback.Intentos, maxRetries);               // Log de retry pendiente
            }

            return false;                                                      // No se envió
        }

        /// <summary>
        /// Convierte una cadena CSV de pedido IDs a lista.
        /// "1772,1773" → ["1772", "1773"]
        /// </summary>
        private List<string> ParsePedidoIds(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return new List<string>();          // Si vacío, retornar lista vacía
            return csv.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList(); // Split por coma, filtrar vacíos
        }
    }
}
