using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using log4net;
using Newtonsoft.Json;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Queue;

namespace PrinterServices.Api.Controllers
{
    /// <summary>
    /// CONTROLLER: Dashboard en tiempo real de PrinterServices.
    /// PROPÓSITO: Exponer endpoint HTML con JavaScript para monitoreo live.
    /// PRINCIPIO: No invasivo, solo lee datos existentes, no modifica lógica de negocio.
    /// </summary>
    public class DashboardController
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(DashboardController));
        private readonly PrinterServiceDb _db;
        private readonly PrintJobManager _jobManager;
        private readonly ConfigManager _config;
        private readonly DateTime _serviceStartTime;

        // Worker de mantenimiento de BD. Opcional (null = endpoints /db-maintenance
        // devuelven 503). Se inyecta desde Host/ApiRouter para que el dashboard
        // pueda disparar purga manual y mostrar info de tamaño.
        private readonly PrinterServices.Workers.DbMaintenanceWorker _dbMaintenanceWorker;

        // Servicio de descubrimiento multi-protocolo de impresoras. Opcional (null =
        // los endpoints /discovery/* devuelven 503). Maneja sesiones en memoria con
        // threading aparte — no afecta al StatusMonitor ni al PrintWorker.
        private readonly PrinterServices.Services.Discovery.PrinterDiscoveryService _discoveryService;

        public DashboardController(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager config, DateTime serviceStartTime)
            : this(db, jobManager, config, serviceStartTime, null, null) { }

        public DashboardController(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager config, DateTime serviceStartTime,
            PrinterServices.Workers.DbMaintenanceWorker dbMaintenanceWorker)
            : this(db, jobManager, config, serviceStartTime, dbMaintenanceWorker, null) { }

        public DashboardController(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager config, DateTime serviceStartTime,
            PrinterServices.Workers.DbMaintenanceWorker dbMaintenanceWorker,
            PrinterServices.Services.Discovery.PrinterDiscoveryService discoveryService)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _serviceStartTime = serviceStartTime;
            _dbMaintenanceWorker = dbMaintenanceWorker;
            _discoveryService = discoveryService;
        }

        /// <summary>
        /// GET /api/dashboard — Retorna HTML del dashboard desde recurso embebido.
        /// RAZÓN: Servir interfaz web de monitoreo en tiempo real.
        /// </summary>
        public void HandleDashboard(HttpListenerContext ctx)
        {
            try
            {
                // RAZÓN: Leer HTML desde recurso embebido
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "PrinterServices.Resources.dashboard.html";
                
                string html;
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Log.Error("[DASHBOARD] No se encontró recurso embebido: " + resourceName);
                        ctx.Response.StatusCode = 500;
                        ctx.Response.Close();
                        return;
                    }
                    
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        html = reader.ReadToEnd();
                    }
                }

                byte[] buffer = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                Log.Debug("[DASHBOARD] Cliente desconectado antes de completar respuesta (html)");
                try { ctx.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("[DASHBOARD] Error sirviendo HTML: " + ex.Message, ex);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// GET /api/dashboard/job/{jobId} — Retorna detalle completo de un job.
        /// Incluye: info del job, capacidades actuales de la impresora, intentos
        /// registrados (print_job_attempts) y el transcript de comandos ESC/POS
        /// (print_job_attempt_commands) por intento. El modal lo renderiza en
        /// secciones expandibles.
        /// </summary>
        public void HandleJobDetail(HttpListenerContext ctx, string jobId)
        {
            try
            {
                // RAZÓN: Buscar job en tabla print_jobs
                var job = _db.Table<Data.Models.PrintJobEntity>()
                    .FirstOrDefault(j => j.JobId == jobId);

                if (job == null)
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                // RAZÓN: Obtener nombre de impresora
                var printer = _db.Table<Data.Models.PrinterEntity>()
                    .FirstOrDefault(p => p.ImpresoraId == job.ImpresoraId);

                // Calcular duración total del job si está terminado.
                long? duracionMs = CalcularDuracionMs(job.FechaCreacion, job.FechaImpresion);

                // Decodificar el DLE EOT del pre-check a legenda humana usando el
                // mismo parser del reporte de conectividad (fuente de verdad única).
                string dleLegend = null;
                if (!string.IsNullOrEmpty(job.PrinterResponse))
                {
                    try { dleLegend = Monitoring.PrinterStatusChecker.Explain(job.PrinterResponse); }
                    catch { dleLegend = null; }
                }

                // Snapshot de capacidades actuales de la impresora (pueden haber
                // sido actualizadas después del job, pero es lo mejor que tenemos
                // hasta que PR 4 persista las caps POR INTENTO).
                object caps = null;
                if (printer != null)
                {
                    caps = new
                    {
                        profile = printer.CapabilitiesProfile ?? "unknown",
                        supportsDleEot = printer.SupportsDleEot == 1,
                        supportsDleEotBits = printer.SupportsDleEotBits,
                        supportsAsb = printer.SupportsAsb == 1,
                        supportsProcessIdResponse = printer.SupportsProcessIdResponse == 1,
                        firmwareParsed = printer.FirmwareParsed,
                        firmwareRaw = printer.FirmwareRaw,
                        detectedAt = printer.CapabilitiesDetectedAt,
                        probeCount = printer.CapabilitiesProbeCount,
                        probeDurationMs = printer.CapabilitiesProbeDurationMs,
                        lastTrigger = printer.CapabilitiesLastTrigger,
                        lastError = printer.CapabilitiesLastError
                    };
                }

                // Intentos registrados para este job (tabla print_job_attempts).
                // Si el job fue creado antes de PR 1 o si PR 4 no está integrado todavía,
                // la lista estará vacía — el front muestra un hint.
                var intentos = _db.Query<Data.Models.PrintJobAttemptEntity>(
                    "SELECT * FROM print_job_attempts WHERE job_id = ? ORDER BY attempt_number ASC",
                    jobId);

                // Ids de intentos para cargar comandos en UNA sola query.
                var intentoIds = new List<long>();
                foreach (var it in intentos) intentoIds.Add(it.Id);

                // Transcript de comandos de todos los intentos de este job.
                // Se agrupa en el response por attempt_id para que el front lo renderice.
                List<Data.Models.PrintJobAttemptCommandEntity> comandosTodos = new List<Data.Models.PrintJobAttemptCommandEntity>();
                if (intentoIds.Count > 0)
                {
                    string inClause = string.Join(",", intentoIds);
                    // inClause viene de ids internos numéricos, no de user input — seguro.
                    comandosTodos = _db.Query<Data.Models.PrintJobAttemptCommandEntity>(
                        "SELECT * FROM print_job_attempt_commands WHERE attempt_id IN (" + inClause + ") " +
                        "ORDER BY attempt_id ASC, sequence_num ASC");
                }

                var intentosSerializables = intentos.Select(i => new
                {
                    id = i.Id,
                    attemptNumber = i.AttemptNumber,
                    startedAt = i.StartedAt,
                    startedAtLocal = i.StartedAtLocal,
                    endedAt = i.EndedAt,
                    endedAtLocal = i.EndedAtLocal,
                    outcome = i.Outcome,
                    failReason = i.FailReason,
                    printerResponsePre = i.PrinterResponsePre,
                    printerResponsePreLegend = i.PrinterResponsePreLegend,
                    printerResponsePost = i.PrinterResponsePost,
                    printerResponsePostLegend = i.PrinterResponsePostLegend,
                    portLockWaitMs = i.PortLockWaitMs,
                    tcpConnectMs = i.TcpConnectMs,
                    dataSendMs = i.DataSendMs,
                    postPrintWaitEstimatedMs = i.PostPrintWaitEstimatedMs,
                    postPrintWaitActualMs = i.PostPrintWaitActualMs,
                    payloadBytes = i.PayloadBytes,
                    copies = i.Copies,
                    exceptionType = i.ExceptionType,
                    exceptionMessage = i.ExceptionMessage,
                    confirmationMethod = i.ConfirmationMethod,
                    confirmationResult = i.ConfirmationResult,
                    confirmationDetail = i.ConfirmationDetail,
                    suspiciousFastSend = i.SuspiciousFastSend == 1,
                    finalDecisionRationale = i.FinalDecisionRationale,
                    // Subset de comandos de este intento
                    commands = comandosTodos.Where(c => c.AttemptId == i.Id).Select(c => new
                    {
                        sequenceNum = c.SequenceNum,
                        phase = c.Phase,
                        direction = c.Direction,
                        commandName = c.CommandName,
                        bytesHex = c.BytesHex,
                        bytesLength = c.BytesLength,
                        bytesSha256 = c.BytesSha256,
                        timestampUtc = c.TimestampUtc,
                        timestampLocal = c.TimestampLocal,
                        offsetMs = c.OffsetMs,
                        durationMs = c.DurationMs,
                        notes = c.Notes
                    }).ToList()
                }).ToList();

                var data = new
                {
                    jobId = job.JobId,
                    comandaId = job.ComandaId,
                    impresoraId = job.ImpresoraId,
                    impresoraNombre = printer?.Nombre ?? job.ImpresoraId,
                    impresoraIp = job.ImpresoraIp,
                    puerto = printer?.Puerto ?? 9100,
                    modelo = job.PrinterModel,
                    estado = job.Estado,
                    reintentos = job.Reintentos,
                    maxReintentos = job.MaxReintentos,
                    copias = job.Copias,
                    prioridad = job.Prioridad,
                    tipoImpresion = job.TipoImpresion,
                    modoImpresion = job.ModoImpresion,
                    deviceIdOrigen = job.DeviceIdOrigen,
                    ipOrigen = job.IpOrigen,
                    areaImpresion = job.AreaImpresion ?? "", // Área de producción (ej: "COCINA AUXILIAR")
                    fechaCreacion = job.FechaCreacion,
                    fechaImpresion = job.FechaImpresion,
                    duracionMs = duracionMs,
                    errorMensaje = job.ErrorMensaje,
                    printerResponse = job.PrinterResponse,
                    printerResponseLegend = dleLegend,
                    contenido = job.Contenido,
                    abreGaveta = job.AbreGaveta,
                    codigoCorte = job.CodigoCorte,
                    // Secciones nuevas: capacidades + intentos con su transcript
                    capabilities = caps,
                    intentos = intentosSerializables,
                    intentosCount = intentosSerializables.Count
                };

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                Log.Debug("[DASHBOARD] Cliente desconectado antes de completar respuesta (job-detail)");
                try { ctx.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("[DASHBOARD] Error obteniendo detalle de job: " + ex.Message, ex);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// GET /api/dashboard/history?page=1&limit=10 — Retorna historial paginado.
        /// RAZÓN: Dashboard con paginación de historial.
        /// </summary>
        public void HandleHistory(HttpListenerContext ctx, int page, int limit)
        {
            try
            {
                // RAZÓN: Validar parámetros
                if (page < 1) page = 1;
                if (limit < 1 || limit > 100) limit = 10;

                int offset = (page - 1) * limit;

                // RAZÓN: Obtener total de registros
                var total = _db.Table<Data.Models.PrintLogEntity>().Count();

                // RAZÓN: Obtener página de historial
                var logs = _db.Table<Data.Models.PrintLogEntity>()
                    .OrderByDescending(l => l.Fecha)
                    .Skip(offset)
                    .Take(limit)
                    .ToList();

                // RAZÓN: Obtener nombres de impresoras para JOIN
                // Usar GroupBy para evitar excepción por ImpresoraId duplicado o null
                var printerNames = _db.Table<Data.Models.PrinterEntity>()
                    .ToList()
                    .Where(p => !string.IsNullOrEmpty(p.ImpresoraId))
                    .GroupBy(p => p.ImpresoraId)
                    .ToDictionary(g => g.Key, g => g.First().Nombre);

                var items = logs.Select(l => new
                {
                    jobId = l.JobId,
                    comandaId = l.JobId, // PrintLogEntity no tiene ComandaId
                    impresoraId = l.ImpresoraId,
                    impresoraNombre = (!string.IsNullOrEmpty(l.ImpresoraId) && printerNames.ContainsKey(l.ImpresoraId)) ? printerNames[l.ImpresoraId] : (l.ImpresoraNombre ?? l.ImpresoraId ?? "desconocida"),
                    areaImpresion = l.AreaImpresion ?? "", // Área de producción
                    estado = l.Estado,
                    fecha = l.Fecha,
                    duracionMs = 0, // TODO: Calcular de timestamps
                    errorMensaje = l.Mensaje ?? "" // Usa Mensaje en lugar de Error
                }).ToList();

                var data = new
                {
                    page = page,
                    limit = limit,
                    total = total,
                    totalPages = (int)Math.Ceiling((double)total / limit),
                    items = items
                };

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                Log.Debug("[DASHBOARD] Cliente desconectado antes de completar respuesta (history)");
                try { ctx.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("[DASHBOARD] Error obteniendo historial: " + ex.Message, ex);
                try
                {
                    string errorJson = JsonConvert.SerializeObject(new { error = ex.Message });
                    byte[] errBuf = Encoding.UTF8.GetBytes(errorJson);
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentLength64 = errBuf.Length;
                    ctx.Response.OutputStream.Write(errBuf, 0, errBuf.Length);
                    ctx.Response.Close();
                }
                catch { ctx.Response.Close(); }
            }
        }

        /// <summary>
        /// GET /api/dashboard/notifications?page=1&limit=50 — Retorna historial paginado de notificaciones (callbacks).
        /// RAZÓN: Dashboard de monitoreo para ver el estado de callbacks a clientes/servidor.
        /// Incluye datos del job (comanda, contenido) vía JOIN con print_jobs.
        /// </summary>
        public void HandleNotificationHistory(HttpListenerContext ctx, int page, int limit)
        {
            try
            {
                if (page < 1) page = 1;                                    // Validar página mínima
                if (limit < 1 || limit > 100) limit = 50;                  // Default 50, máximo 100

                int offset = (page - 1) * limit;                           // Calcular offset para paginación

                // Obtener total de registros para calcular totalPages
                var total = _db.Table<Data.Models.JobStatusCallbackEntity>().Count();

                // Obtener página de callbacks ordenados por fecha_creacion DESC (más reciente primero)
                var callbacks = _db.Table<Data.Models.JobStatusCallbackEntity>()
                    .OrderByDescending(c => c.FechaCreacion)
                    .Skip(offset)
                    .Take(limit)
                    .ToList();

                // JOIN manual: obtener datos del job (comanda, contenido, impresora) desde print_jobs
                // Recopilar jobIds únicos para buscar en una sola query
                var jobIds = callbacks
                    .Where(c => !string.IsNullOrEmpty(c.JobId))
                    .Select(c => c.JobId)
                    .Distinct()
                    .ToList();

                // Buscar jobs en BD para obtener comanda y contenido
                var jobsDict = new Dictionary<string, Data.Models.PrintJobEntity>();
                foreach (var jid in jobIds)
                {
                    var job = _db.Table<Data.Models.PrintJobEntity>()
                        .FirstOrDefault(j => j.JobId == jid);
                    if (job != null)
                    {
                        jobsDict[jid] = job;
                    }
                }

                // Construir items de respuesta con datos enriquecidos del job
                var items = callbacks.Select(c =>
                {
                    Data.Models.PrintJobEntity job = null;
                    if (!string.IsNullOrEmpty(c.JobId) && jobsDict.ContainsKey(c.JobId))
                    {
                        job = jobsDict[c.JobId];
                    }

                    return new
                    {
                        id = c.Id,                                         // PK del callback
                        jobId = c.JobId,                                   // ID del job de impresión
                        statusJob = c.Status,                              // Estado del job notificado (DONE/FAILED/etc.)
                        pedidoIds = c.PedidoIds,                           // Pedidos asociados (CSV)
                        ipDestino = c.IpOrigen,                            // IP destino del callback
                        esCliente = c.EsCliente == 1,                      // true=cliente(:8083), false=servidor(:8081)
                        estadoEnvio = c.EstadoEnvio,                       // PENDIENTE/ENVIADO/FALLIDO
                        intentos = c.Intentos,                             // Cantidad de intentos de envío
                        ultimoError = c.UltimoError ?? "",                 // Último error HTTP
                        error = c.Error ?? "",                             // Error del job (sin papel, etc.)
                        fechaCreacion = c.FechaCreacion.ToString("o"),     // ISO8601
                        fechaEnvio = c.FechaEnvio.HasValue
                            ? c.FechaEnvio.Value.ToString("o") : null,     // ISO8601 o null
                        // Datos enriquecidos del job (JOIN con print_jobs)
                        comandaId = job != null ? job.ComandaId : null,    // Comanda asociada
                        impresoraNombre = job != null ? (job.ImpresoraNombre ?? job.ImpresoraId) : null,
                        areaImpresion = job != null ? (job.AreaImpresion ?? "") : "",
                        contenido = job != null ? job.Contenido : null     // Contenido de impresión (ESC/POS)
                    };
                }).ToList();

                var data = new
                {
                    page = page,
                    limit = limit,
                    total = total,
                    totalPages = (int)Math.Ceiling((double)total / limit),
                    items = items
                };

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                Log.Debug("[DASHBOARD] Cliente desconectado antes de completar respuesta (notifications)");
                try { ctx.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("[DASHBOARD] Error obteniendo historial de notificaciones: " + ex.Message, ex);
                try
                {
                    string errorJson = JsonConvert.SerializeObject(new { error = ex.Message });
                    byte[] errBuf = Encoding.UTF8.GetBytes(errorJson);
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentLength64 = errBuf.Length;
                    ctx.Response.OutputStream.Write(errBuf, 0, errBuf.Length);
                    ctx.Response.Close();
                }
                catch { ctx.Response.Close(); }
            }
        }

        /// <summary>
        /// GET /api/dashboard/data — Retorna JSON con toda la info del sistema.
        /// RAZÓN: Endpoint consumido por el dashboard cada N segundos para refresh.
        /// </summary>
        public void HandleDashboardData(HttpListenerContext ctx)
        {
            try
            {
                var data = CollectSystemData();
                string json = JsonConvert.SerializeObject(data, Formatting.Indented);

                byte[] buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                Log.Debug("[DASHBOARD] Cliente desconectado antes de completar respuesta (data)");
                try { ctx.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("[DASHBOARD] Error obteniendo datos: " + ex.Message, ex);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// GET /api/dashboard/network-history?page=1&limit=20 — Historial completo de alertas de red.
        /// RAZÓN: Ver cuándo cambió la red, cuándo se detectaron problemas de latencia, etc.
        /// </summary>
        public void HandleNetworkHistory(HttpListenerContext ctx, int page, int limit)
        {
            try
            {
                if (page < 1) page = 1;
                if (limit < 1 || limit > 100) limit = 20;

                int offset = (page - 1) * limit;

                var total = _db.Table<Data.Models.NetworkAlertEntity>().Count();

                var alerts = _db.Table<Data.Models.NetworkAlertEntity>()
                    .OrderByDescending(a => a.DetectedAt)
                    .Skip(offset)
                    .Take(limit)
                    .Select(a => new
                    {
                        alertId = a.AlertId,
                        alertType = a.AlertType,
                        severity = a.Severity,
                        previousGatewayMac = a.PreviousGatewayMac,
                        currentGatewayMac = a.CurrentGatewayMac,
                        previousNetworkId = a.PreviousNetworkId,
                        currentNetworkId = a.CurrentNetworkId,
                        message = a.Message,
                        detectedAt = a.DetectedAt.ToString("o"),
                        notified = a.Notified == 1
                    })
                    .ToList();

                // También incluir historial de cambios de IP de impresoras
                var ipChanges = _db.Table<Data.Models.IpChangeNotificationEntity>()
                    .OrderByDescending(c => c.FechaCreacion)
                    .Take(50)
                    .Select(c => new
                    {
                        macAddress = c.MacAddress,
                        oldIp = c.OldIp,
                        newIp = c.NewIp,
                        estado = c.Estado,
                        fechaCreacion = c.FechaCreacion.ToString("o"),
                        fechaEnvio = c.FechaEnvio.HasValue ? c.FechaEnvio.Value.ToString("o") : null,
                        intentos = c.Intentos
                    })
                    .ToList();

                var data = new
                {
                    page = page,
                    limit = limit,
                    total = total,
                    totalPages = (int)Math.Ceiling((double)total / limit),
                    alerts = alerts,
                    ipChanges = ipChanges
                };

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                Log.Debug("[DASHBOARD] Cliente desconectado antes de completar respuesta (network-history)");
                try { ctx.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("[DASHBOARD] Error obteniendo historial de red: " + ex.Message, ex);
                try
                {
                    string errorJson = JsonConvert.SerializeObject(new { error = ex.Message });
                    byte[] errBuf = Encoding.UTF8.GetBytes(errorJson);
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentLength64 = errBuf.Length;
                    ctx.Response.OutputStream.Write(errBuf, 0, errBuf.Length);
                    ctx.Response.Close();
                }
                catch { ctx.Response.Close(); }
            }
        }

        /// <summary>
        /// GET /api/dashboard/connectivity?page=1&limit=50&impresora_id= — Reporte de conectividad.
        /// RAZÓN: Ver a qué hora se desconectó una impresora y a qué hora volvió.
        /// </summary>
        public void HandleConnectivityReport(HttpListenerContext ctx, int page, int limit, string impresoraIdFilter)
        {
            try
            {
                if (page < 1) page = 1;
                if (limit < 1 || limit > 200) limit = 50;

                int offset = (page - 1) * limit;

                // Filtrar por impresora si se especifica
                List<Data.Models.PrinterStatusLogEntity> logs;
                int total;

                if (!string.IsNullOrEmpty(impresoraIdFilter))
                {
                    total = _db.Table<Data.Models.PrinterStatusLogEntity>()
                        .Where(l => l.ImpresoraId == impresoraIdFilter)
                        .Count();
                    logs = _db.Table<Data.Models.PrinterStatusLogEntity>()
                        .Where(l => l.ImpresoraId == impresoraIdFilter)
                        .OrderByDescending(l => l.Fecha)
                        .Skip(offset)
                        .Take(limit)
                        .ToList();
                }
                else
                {
                    total = _db.Table<Data.Models.PrinterStatusLogEntity>().Count();
                    logs = _db.Table<Data.Models.PrinterStatusLogEntity>()
                        .OrderByDescending(l => l.Fecha)
                        .Skip(offset)
                        .Take(limit)
                        .ToList();
                }

                var items = logs.Select(l => new
                {
                    id = l.Id,
                    impresoraId = l.ImpresoraId,
                    impresoraNombre = l.ImpresoraNombre,
                    impresoraIp = l.ImpresoraIp,
                    macAddress = l.MacAddress,
                    estadoAnterior = l.EstadoAnterior,
                    estadoNuevo = l.EstadoNuevo,
                    detalle = l.Detalle,
                    printerResponse = l.PrinterResponse,
                    printerResponseLegend = l.PrinterResponseLegend,
                    fecha = l.Fecha
                }).ToList();

                // Lista de impresoras para filtro en el frontend
                var printerList = _db.Table<Data.Models.PrinterEntity>()
                    .OrderBy(p => p.Nombre)
                    .Select(p => new { id = p.ImpresoraId, nombre = p.Nombre })
                    .ToList();

                var data = new
                {
                    page = page,
                    limit = limit,
                    total = total,
                    totalPages = (int)Math.Ceiling((double)total / limit),
                    items = items,
                    printers = printerList
                };

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                Log.Debug("[DASHBOARD] Cliente desconectado antes de completar respuesta (connectivity)");
                try { ctx.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("[DASHBOARD] Error obteniendo reporte de conectividad: " + ex.Message, ex);
                try
                {
                    string errorJson = JsonConvert.SerializeObject(new { error = ex.Message });
                    byte[] errBuf = Encoding.UTF8.GetBytes(errorJson);
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentLength64 = errBuf.Length;
                    ctx.Response.OutputStream.Write(errBuf, 0, errBuf.Length);
                    ctx.Response.Close();
                }
                catch { ctx.Response.Close(); }
            }
        }

        /// <summary>
        /// Recopila toda la información del sistema para el dashboard.
        /// RAZÓN: Single source of truth para el estado completo.
        /// </summary>
        private object CollectSystemData()
        {
            // RAZÓN: Obtener todas las impresoras con su estado completo
            var printers = _db.Table<Data.Models.PrinterEntity>()
                .OrderBy(p => p.Nombre)
                .Select(p => new
                {
                    id = p.ImpresoraId,
                    nombre = p.Nombre,
                    ip = p.Ip,
                    puerto = p.Puerto,
                    mac = p.MacAddress,
                    modelo = p.Modelo,
                    estadoConexion = p.EstadoOnline == 1 ? "ONLINE" : "OFFLINE",
                    estadoDisponibilidad = p.TienePapel == 1 ? "CON_PAPEL" : "SIN_PAPEL",
                    disponibleParaImprimir = p.DisponibleParaImprimir == 1,
                    lastCheck = p.UltimoCheck,
                    ipResueltaPorArp = p.IpResueltaPorArp == 1
                })
                .ToList();

            // RAZÓN: Obtener estadísticas de latencias por impresora
            var latencyStats = _db.Table<Data.Models.PrinterLatencyStatsEntity>()
                .ToList()
                .ToDictionary(s => s.ImpresoraId, s => new
                {
                    baselineAvgMs = s.BaselineAvgMs ?? 0,
                    baselineP95Ms = s.BaselineP95Ms ?? 0,
                    last24hAvgMs = s.Last24hAvgMs ?? 0,
                    last24hMaxMs = s.Last24hMaxMs ?? 0,
                    last24hP95Ms = s.Last24hP95Ms ?? 0,
                    last24hPrints = s.Last24hPrints,
                    isDegraded = s.IsDegraded == 1,
                    degradationFactor = s.DegradationFactor ?? 0,
                    degradationSince = s.DegradationSince
                });

            // RAZÓN: Obtener estado actual de red
            var networkCurrent = _db.Table<Data.Models.NetworkCurrentEntity>()
                .FirstOrDefault(c => c.Id == 1);

            // RAZÓN: Obtener últimas 5 alertas de red
            var networkAlerts = _db.Table<Data.Models.NetworkAlertEntity>()
                .OrderByDescending(a => a.DetectedAt)
                .Take(5)
                .Select(a => new
                {
                    severity = a.Severity,
                    alertType = a.AlertType,
                    message = a.Message,
                    createdAt = a.DetectedAt
                })
                .ToList();

            // RAZÓN: Obtener cola de impresión desde BD (trabajos pendientes y en progreso)
            var queueSnapshot = _db.Table<Data.Models.PrintJobEntity>()
                .Where(j => j.Estado == "PENDING" || j.Estado == "WAITING" || j.Estado == "PRINTING")
                .ToList();
            
            // RAZÓN: Obtener nombres de impresoras para JOIN manual
            var printerNames = _db.Table<Data.Models.PrinterEntity>()
                .ToDictionary(p => p.ImpresoraId, p => p.Nombre);
            
            var pendingJobs = queueSnapshot
                .Where(j => j.Estado == "PENDING" || j.Estado == "WAITING")
                .Select(j => new
                {
                    jobId = j.JobId,
                    impresoraId = j.ImpresoraId,
                    impresoraNombre = printerNames.ContainsKey(j.ImpresoraId) ? printerNames[j.ImpresoraId] : j.ImpresoraId,
                    estado = j.Estado,
                    reintentos = j.Reintentos,
                    maxReintentos = j.MaxReintentos,
                    createdAt = j.FechaCreacion,
                    errorMensaje = j.ErrorMensaje ?? ""
                })
                .ToList();

            // RAZÓN: Obtener estadísticas de impresiones (últimas 24h)
            // Se agrupa por JobId para contar jobs únicos, no registros duplicados de log.
            // Cada job puede generar múltiples logs (WAITING, RETRY, DONE, FAILED, etc.)
            var now = DateTime.Now;
            var last24h = now.AddHours(-24);
            var printLogs = _db.Table<Data.Models.PrintLogEntity>()
                .Where(l => l.Fecha != null)
                .ToList()
                .Where(l => DateTime.TryParse(l.Fecha, out DateTime fecha) && fecha >= last24h)
                .ToList();

            // RAZÓN: Agrupar por JobId y tomar el último estado de cada job único
            var uniqueJobs = printLogs
                .GroupBy(l => l.JobId)
                .Select(g => g.OrderByDescending(l => l.Fecha).First())
                .ToList();

            var totalJobs = uniqueJobs.Count;
            var exitosas = uniqueJobs.Count(l => l.Estado == "DONE");
            var fallidas = uniqueJobs.Count(l => l.Estado == "FAILED");
            var esperando = uniqueJobs.Count(l => l.Estado == "WAITING");
            var enProceso = uniqueJobs.Count(l => l.Estado == "PRINTING" || l.Estado == "PENDING");
            var otros = totalJobs - exitosas - fallidas - esperando - enProceso;

            var printStats = new
            {
                total24h = totalJobs,
                exitosas24h = exitosas,
                fallidas24h = fallidas,
                esperando24h = esperando,
                enProceso24h = enProceso,
                otros24h = otros
            };

            // RAZÓN: Obtener últimas 10 latencias para gráfica
            var recentLatencies = _db.Table<Data.Models.PrintLatencyLogEntity>()
                .OrderByDescending(l => l.CreatedAt)
                .Take(10)
                .Select(l => new
                {
                    jobId = l.JobId,
                    impresoraId = l.ImpresoraId,
                    totalPrintMs = l.TotalPrintMs,
                    tcpConnectMs = l.TcpConnectMs,
                    dataSendMs = l.DataSendMs,
                    success = l.Success == 1,
                    createdAt = l.CreatedAt
                })
                .ToList();

            // RAZÓN: Construir response completo
            return new
            {
                version = Core.ServiceVersion.FullVersion,
                timestamp = DateTime.Now.ToString("o"),
                network = new
                {
                    current = networkCurrent != null ? new
                    {
                        gatewayMac = networkCurrent.GatewayMac,
                        gatewayIp = networkCurrent.GatewayIp,
                        printerServiceIp = networkCurrent.PrinterServiceIp,
                        networkId = networkCurrent.NetworkId,
                        wifiSsid = networkCurrent.WifiSsid,
                        status = networkCurrent.Status,
                        lastCheck = networkCurrent.LastCheck
                    } : null,
                    recentAlerts = networkAlerts
                },
                printers = printers.Select(p => new
                {
                    p.id,
                    p.nombre,
                    p.ip,
                    p.puerto,
                    p.mac,
                    p.modelo,
                    p.estadoConexion,
                    p.estadoDisponibilidad,
                    p.disponibleParaImprimir,
                    p.lastCheck,
                    p.ipResueltaPorArp,
                    latencyStats = latencyStats.ContainsKey(p.id) ? latencyStats[p.id] : null
                }),
                queue = new
                {
                    pending = queueSnapshot.Count(j => j.Estado == "PENDING"),
                    waiting = queueSnapshot.Count(j => j.Estado == "WAITING"),
                    printing = queueSnapshot.Count(j => j.Estado == "PRINTING"),
                    jobs = pendingJobs
                },
                stats = printStats,
                recentLatencies = recentLatencies,
                system = Workers.SystemInfoCollector.CollectAll(_serviceStartTime),
                latestSpeed = GetLatestSpeed(),
                networkDevicesCount = GetNetworkDevicesCount()
            };
        }

        private object GetLatestSpeed()
        {
            try
            {
                var latest = _db.Table<Data.Models.NetworkSpeedLogEntity>().OrderByDescending(s => s.MeasuredAt).FirstOrDefault();
                if (latest == null) return null;
                return new { downloadSpeedKbps = latest.DownloadSpeedKbps, latencyMs = latest.LatencyMs, measuredAt = latest.MeasuredAt };
            }
            catch { return null; }
        }

        private object GetNetworkDevicesCount()
        {
            try
            {
                var devices = _db.Table<Data.Models.DeviceOnNetworkEntity>().ToList();
                return new { total = devices.Count, online = devices.Count(d => d.IsOnline == 1) };
            }
            catch { return null; }
        }

        public void HandleSpeedHistory(HttpListenerContext ctx, int limit)
        {
            try
            {
                var history = _db.Table<Data.Models.NetworkSpeedLogEntity>().OrderByDescending(s => s.MeasuredAt).Take(limit).ToList();
                history.Reverse();
                WriteJsonResponse(ctx, 200, new { count = history.Count, data = history.Select(s => new { s.DownloadSpeedKbps, s.LatencyMs, s.GatewayIp, s.NetworkId, s.MeasuredAt }) });
            }
            catch (Exception ex) { WriteJsonResponse(ctx, 500, new { error = ex.Message }); }
        }

        public void HandleNetworkDevices(HttpListenerContext ctx)
        {
            try
            {
                var devices = _db.Table<Data.Models.DeviceOnNetworkEntity>().OrderByDescending(d => d.IsOnline).ThenByDescending(d => d.LastSeenAt).ToList();
                WriteJsonResponse(ctx, 200, new { count = devices.Count, onlineCount = devices.Count(d => d.IsOnline == 1),
                    data = devices.Select(d => new { ip = d.IpAddress, mac = d.MacAddress, hostname = d.Hostname, vendor = d.Vendor, deviceType = d.DeviceType, isOnline = d.IsOnline == 1, firstSeen = d.FirstSeenAt, lastSeen = d.LastSeenAt, networkId = d.NetworkId, gatewayMac = d.GatewayMac }) });
            }
            catch (Exception ex) { WriteJsonResponse(ctx, 500, new { error = ex.Message }); }
        }

        public void HandleSqlQuery(HttpListenerContext ctx)
        {
            try
            {
                var remoteIp = ctx.Request.RemoteEndPoint.Address.ToString();
                if (remoteIp != "127.0.0.1" && remoteIp != "::1" && remoteIp != "0:0:0:0:0:0:0:1")
                { WriteJsonResponse(ctx, 403, new { error = "Solo accesible desde localhost" }); return; }

                string body;
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = reader.ReadToEnd();

                var request = JsonConvert.DeserializeAnonymousType(body, new { query = "", confirmed = false });
                if (request == null || string.IsNullOrWhiteSpace(request.query))
                { WriteJsonResponse(ctx, 400, new { error = "Se requiere el campo 'query'" }); return; }

                var query = request.query.Trim();
                var queryUpper = query.ToUpperInvariant();

                var blacklist = new[] { "DROP TABLE", "DROP INDEX", "ALTER TABLE", "PRAGMA JOURNAL_MODE", "ATTACH", "DETACH" };
                foreach (var cmd in blacklist)
                    if (queryUpper.Contains(cmd)) { WriteJsonResponse(ctx, 403, new { error = "Comando no permitido: " + cmd }); return; }

                Log.Info("[SQL-CONSOLE] Ejecutando: " + query);
                bool isSelect = queryUpper.StartsWith("SELECT") || queryUpper.StartsWith("PRAGMA");
                bool isWrite = queryUpper.StartsWith("INSERT") || queryUpper.StartsWith("UPDATE") || queryUpper.StartsWith("DELETE") || queryUpper.StartsWith("CREATE");

                if (isWrite && !request.confirmed)
                { WriteJsonResponse(ctx, 200, new { requiresConfirmation = true, queryType = queryUpper.Split(' ')[0], message = "Confirme para continuar." }); return; }

                if (isSelect)
                {
                    // Usar API nativa SQLite3 para queries arbitrarias (PSQLite Query<T> requiere tipo mapeado)
                    var columns = new List<string>();
                    var rows = new List<Dictionary<string, object>>();
                    var stmt = PSQLite.SQLite3.Prepare2(_db.Handle, query);
                    try
                    {
                        int colCount = PSQLite.SQLite3.ColumnCount(stmt);
                        for (int i = 0; i < colCount; i++)
                            columns.Add(PSQLite.SQLite3.ColumnName16(stmt, i));

                        while (PSQLite.SQLite3.Step(stmt) == PSQLite.SQLite3.Result.Row && rows.Count < 500)
                        {
                            var row = new Dictionary<string, object>();
                            for (int i = 0; i < colCount; i++)
                            {
                                var colType = PSQLite.SQLite3.ColumnType(stmt, i);
                                object val;
                                switch (colType)
                                {
                                    case PSQLite.SQLite3.ColType.Integer: val = PSQLite.SQLite3.ColumnInt64(stmt, i); break;
                                    case PSQLite.SQLite3.ColType.Float: val = PSQLite.SQLite3.ColumnDouble(stmt, i); break;
                                    case PSQLite.SQLite3.ColType.Text: val = System.Runtime.InteropServices.Marshal.PtrToStringUni(PSQLite.SQLite3.ColumnText16(stmt, i)); break;
                                    case PSQLite.SQLite3.ColType.Null: val = null; break;
                                    default: val = System.Runtime.InteropServices.Marshal.PtrToStringUni(PSQLite.SQLite3.ColumnText16(stmt, i)); break;
                                }
                                row[columns[i]] = val;
                            }
                            rows.Add(row);
                        }
                    }
                    finally { PSQLite.SQLite3.Finalize(stmt); }
                    WriteJsonResponse(ctx, 200, new { success = true, queryType = "SELECT", columns, rows, rowCount = rows.Count, truncated = rows.Count >= 500 });
                }
                else
                {
                    int affected = _db.Execute(query);
                    WriteJsonResponse(ctx, 200, new { success = true, queryType = queryUpper.Split(' ')[0], rowsAffected = affected });
                }
            }
            catch (Exception ex) { WriteJsonResponse(ctx, 200, new { success = false, error = ex.Message }); }
        }

        public void HandleUpdateRequest(HttpListenerContext ctx)
        {
            try
            {
                string body; using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = reader.ReadToEnd();
                string serverIp = FindQuipuNetXIp();
                if (serverIp == null) { WriteJsonResponse(ctx, 503, new { error = "No se pudo determinar IP de QuipuNetX" }); return; }
                var request = (HttpWebRequest)WebRequest.Create("http://" + serverIp + ":8081/api/health/update-printer-service");
                request.Method = "POST"; request.ContentType = "application/json"; request.Timeout = 10000;
                var bodyBytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bodyBytes.Length;
                using (var rs = request.GetRequestStream()) rs.Write(bodyBytes, 0, bodyBytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse()) using (var sr = new System.IO.StreamReader(response.GetResponseStream()))
                    WriteRawJsonResponse(ctx, (int)response.StatusCode, sr.ReadToEnd());
            }
            catch (WebException wex) { WriteJsonResponse(ctx, 503, new { error = "QuipuNetX no disponible: " + wex.Message }); }
            catch (Exception ex) { WriteJsonResponse(ctx, 500, new { error = ex.Message }); }
        }

        public void HandleUpdateStatus(HttpListenerContext ctx)
        {
            try
            {
                string serverIp = FindQuipuNetXIp(); if (serverIp == null) { WriteJsonResponse(ctx, 503, new { error = "No se pudo determinar IP" }); return; }
                var request = (HttpWebRequest)WebRequest.Create("http://" + serverIp + ":8081/api/health/update-printer-service-status"); request.Method = "GET"; request.Timeout = 5000;
                using (var response = (HttpWebResponse)request.GetResponse()) using (var sr = new System.IO.StreamReader(response.GetResponseStream()))
                    WriteRawJsonResponse(ctx, (int)response.StatusCode, sr.ReadToEnd());
            }
            catch (Exception ex) { WriteJsonResponse(ctx, 503, new { error = "QuipuNetX no disponible: " + ex.Message }); }
        }

        public void HandleQuipuNetHealth(HttpListenerContext ctx)
        {
            try
            {
                string serverIp = FindQuipuNetXIp(); if (serverIp == null) { WriteJsonResponse(ctx, 503, new { error = "No se pudo determinar IP" }); return; }
                var request = (HttpWebRequest)WebRequest.Create("http://" + serverIp + ":8081/api/health/status"); request.Method = "GET"; request.Timeout = 5000;
                using (var response = (HttpWebResponse)request.GetResponse()) using (var sr = new System.IO.StreamReader(response.GetResponseStream()))
                    WriteRawJsonResponse(ctx, (int)response.StatusCode, sr.ReadToEnd());
            }
            catch (Exception ex) { WriteJsonResponse(ctx, 503, new { error = "QuipuNetX no disponible: " + ex.Message }); }
        }

        public void HandleQuipuNetScreenshot(HttpListenerContext ctx)
        {
            try
            {
                string serverIp = FindQuipuNetXIp(); if (serverIp == null) { WriteJsonResponse(ctx, 503, new { error = "No se pudo determinar IP" }); return; }
                var request = (HttpWebRequest)WebRequest.Create("http://" + serverIp + ":8081/api/health/screenshot"); request.Method = "GET"; request.Timeout = 15000;
                using (var response = (HttpWebResponse)request.GetResponse()) using (var sr = new System.IO.StreamReader(response.GetResponseStream()))
                    WriteRawJsonResponse(ctx, (int)response.StatusCode, sr.ReadToEnd());
            }
            catch (Exception ex) { WriteJsonResponse(ctx, 503, new { error = "QuipuNetX no disponible: " + ex.Message }); }
        }

        private string FindQuipuNetXIp()
        {
            try { var lj = _db.Table<Data.Models.PrintJobEntity>().OrderByDescending(j => j.FechaCreacion).FirstOrDefault(); if (lj != null && !string.IsNullOrEmpty(lj.IpServidor)) return lj.IpServidor; } catch { }
            return "127.0.0.1";
        }

        // ═════════════════════════════════════════════════════════════════════════
        // Mantenimiento de BD: endpoints para el dashboard
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET /api/dashboard/db-maintenance/info
        /// Devuelve tamaño actual del archivo .db, máximo configurado, top tablas por
        /// cantidad de filas, y datos de la última purga (cuándo, modo, filas eliminadas).
        /// </summary>
        public void HandleDbMaintenanceInfo(HttpListenerContext ctx)
        {
            try
            {
                if (_dbMaintenanceWorker == null)
                {
                    WriteJsonResponse(ctx, 503, new { error = "DbMaintenanceWorker no está habilitado en este servicio" });
                    return;
                }

                var info = _dbMaintenanceWorker.ObtenerInfo();
                var resp = new
                {
                    tamanioMb = Math.Round(info.TamanioMb, 2),
                    tamanioMaxMb = info.TamanioMaxMb,
                    excedeMaximo = info.TamanioMb > info.TamanioMaxMb,
                    ultimaPurga = info.UltimaPurga,
                    ultimaPurgaModo = info.UltimaPurgaModo,
                    ultimaPurgaFilasEliminadas = info.UltimaPurgaFilasEliminadas,
                    ultimaPurgaMbLiberados = Math.Round(info.UltimaPurgaBytesLiberados / 1024.0 / 1024.0, 2),
                    conteoPorTabla = info.ConteoPorTabla
                };
                WriteJsonResponse(ctx, 200, resp);
            }
            catch (Exception ex)
            {
                Log.Error("[DB-MAINT-API] Error obteniendo info", ex);
                WriteJsonResponse(ctx, 500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// POST /api/dashboard/db-maintenance/run
        /// Dispara una purga manual inmediata. Devuelve el resumen (modo liviano/agresivo,
        /// filas eliminadas, MB liberados, tamaño antes/después, si hizo VACUUM).
        /// Solo accesible desde localhost (mismo patrón que HandleSqlQuery) porque es
        /// una operación destructiva.
        /// </summary>
        public void HandleDbMaintenanceRun(HttpListenerContext ctx)
        {
            try
            {
                // Solo localhost — evitar que alguien de la red dispare purgas.
                string ipCliente = ctx.Request.RemoteEndPoint != null ? ctx.Request.RemoteEndPoint.Address.ToString() : null;
                if (ipCliente != "127.0.0.1" && ipCliente != "::1" && ipCliente != "localhost")
                {
                    WriteJsonResponse(ctx, 403, new { error = "Purga manual solo accesible desde localhost" });
                    return;
                }

                if (_dbMaintenanceWorker == null)
                {
                    WriteJsonResponse(ctx, 503, new { error = "DbMaintenanceWorker no está habilitado" });
                    return;
                }

                Log.InfoFormat("[DB-MAINT-API] Purga manual solicitada desde {0}", ipCliente);
                var resumen = _dbMaintenanceWorker.PurgarAhora();

                var resp = new
                {
                    exito = string.IsNullOrEmpty(resumen.Error),
                    error = resumen.Error,
                    modo = resumen.Modo,
                    filasEliminadas = resumen.FilasEliminadas,
                    mbLiberados = Math.Round(resumen.BytesLiberados / 1024.0 / 1024.0, 2),
                    tamanioMbAntes = Math.Round(resumen.TamanioMbAntes, 2),
                    tamanioMbDespues = Math.Round(resumen.TamanioMbDespues, 2),
                    vacuumEjecutado = resumen.VacuumEjecutado
                };
                WriteJsonResponse(ctx, string.IsNullOrEmpty(resumen.Error) ? 200 : 500, resp);
            }
            catch (Exception ex)
            {
                Log.Error("[DB-MAINT-API] Error en purga manual", ex);
                WriteJsonResponse(ctx, 500, new { error = ex.Message });
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // Bitmaps de diagnóstico por job (JPG + características)
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET /api/dashboard/job/{id}/bitmap-info — JSON con características del
        /// bitmap guardado para este job (si GuardarBitmapsGenerados estaba activo
        /// cuando se imprimió). Devuelve 404 si no hay registro.
        /// </summary>
        public void HandleJobBitmapInfo(HttpListenerContext ctx, string jobId)
        {
            try
            {
                if (string.IsNullOrEmpty(jobId))
                {
                    WriteJsonResponse(ctx, 400, new { error = "jobId requerido" });
                    return;
                }
                var filas = _db.Query<Data.Models.PrintJobBitmapEntity>(
                    "SELECT * FROM print_job_bitmaps WHERE job_id = ? ORDER BY id DESC",
                    jobId);
                if (filas == null || filas.Count == 0)
                {
                    WriteJsonResponse(ctx, 404, new { error = "Este job no tiene bitmap guardado. Activa GuardarBitmapsGenerados y vuelve a imprimir." });
                    return;
                }
                // Devolver el más reciente (primera fila por ORDER BY DESC).
                var b = filas[0];
                WriteJsonResponse(ctx, 200, new
                {
                    jobId = b.JobId,
                    generatedAtUtc = b.GeneratedAtUtc,
                    generatedAtLocal = b.GeneratedAtLocal,
                    bitmapPath = b.BitmapPath,
                    modo = b.Modo,
                    widthPx = b.WidthPx,
                    heightPx = b.HeightPx,
                    bitsPerPixel = b.BitsPerPixel,
                    fileSizeBytes = b.FileSizeBytes,
                    fileSizeKb = Math.Round(b.FileSizeBytes / 1024.0, 1),
                    escposPayloadBytes = b.EscposPayloadBytes,
                    escposPayloadKb = Math.Round(b.EscposPayloadBytes / 1024.0, 1),
                    darkRatioAvg = Math.Round(b.DarkRatioAvg, 3),
                    darkRatioAvgPct = Math.Round(b.DarkRatioAvg * 100, 1),
                    darkRatioMaxRow = Math.Round(b.DarkRatioMaxRow, 3),
                    darkRatioMaxRowPct = Math.Round(b.DarkRatioMaxRow * 100, 1),
                    estimatedPrintMs = b.EstimatedPrintMs,
                    bitmapEmulacion = b.BitmapEmulacion,
                    printerModel = b.PrinterModel,
                    targetIp = b.TargetIp,
                    imageUrl = "/api/dashboard/job/" + b.JobId + "/bitmap"
                });
            }
            catch (Exception ex)
            {
                Log.Error("[BITMAP-API] Error leyendo info del bitmap", ex);
                WriteJsonResponse(ctx, 500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// GET /api/dashboard/job/{id}/bitmap — sirve el JPG guardado.
        /// Content-Type: image/jpeg. Devuelve 404 si no existe.
        /// </summary>
        public void HandleJobBitmapJpeg(HttpListenerContext ctx, string jobId)
        {
            try
            {
                if (string.IsNullOrEmpty(jobId))
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    return;
                }
                var fila = _db.Query<Data.Models.PrintJobBitmapEntity>(
                    "SELECT * FROM print_job_bitmaps WHERE job_id = ? ORDER BY id DESC LIMIT 1",
                    jobId);
                if (fila == null || fila.Count == 0)
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                string pathAbsoluto = PrinterServices.Services.Printers.BitmapDiagnosticSaver
                    .ResolverPathAbsoluto(fila[0].BitmapPath);
                if (string.IsNullOrEmpty(pathAbsoluto) || !System.IO.File.Exists(pathAbsoluto))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                ctx.Response.ContentType = "image/jpeg";
                var bytes = System.IO.File.ReadAllBytes(pathAbsoluto);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                try { ctx.Response.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("[BITMAP-API] Error sirviendo JPG", ex);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // Descubrimiento multi-protocolo de impresoras en la red
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// POST /api/dashboard/discovery/start
        /// Inicia una sesión de descubrimiento en background (ENPC + mDNS + SNMP + ARP/TCP).
        /// Responde inmediatamente con el sessionId. La UI polling /status/{id} cada
        /// ~800ms para ir viendo los resultados en vivo.
        /// </summary>
        public void HandleDiscoveryStart(HttpListenerContext ctx)
        {
            try
            {
                if (_discoveryService == null)
                {
                    WriteJsonResponse(ctx, 503, new { error = "Servicio de descubrimiento no disponible" });
                    return;
                }
                var sesion = _discoveryService.Iniciar();
                WriteJsonResponse(ctx, 200, new
                {
                    sessionId = sesion.Id,
                    startedAt = sesion.StartedAtLocal,
                    protocolos = sesion.Protocolos.ConvertAll(p => new
                    {
                        nombre = p.Nombre,
                        metodo = p.Metodo,
                        estado = p.Estado
                    })
                });
            }
            catch (Exception ex)
            {
                Log.Error("[DISCOVERY-API] Error iniciando sesión", ex);
                WriteJsonResponse(ctx, 500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// GET /api/dashboard/discovery/status/{id}
        /// Devuelve el estado actual de una sesión + resultados parciales. La UI lo
        /// llama cada ~800ms mientras estado=running y una última vez al completarse.
        /// Cada resultado viene enriquecido con YaAgregadaComoId/Nombre si la MAC
        /// ya está en la tabla printers (el front muestra "Ya agregada como X").
        /// </summary>
        public void HandleDiscoveryStatus(HttpListenerContext ctx, string sessionId)
        {
            try
            {
                if (_discoveryService == null)
                {
                    WriteJsonResponse(ctx, 503, new { error = "Servicio de descubrimiento no disponible" });
                    return;
                }
                var sesion = _discoveryService.Obtener(sessionId);
                if (sesion == null)
                {
                    WriteJsonResponse(ctx, 404, new { error = "Sesión no encontrada o expirada" });
                    return;
                }
                // Refrescar el flag "ya agregada" con el estado actual de BD.
                _discoveryService.EnriquecerConRegistrosExistentes(sesion);

                object resp;
                lock (sesion.SyncRoot)
                {
                    resp = new
                    {
                        sessionId = sesion.Id,
                        estado = sesion.Estado,
                        startedAt = sesion.StartedAtLocal,
                        completedAtUtc = sesion.CompletedAtUtc,
                        duracionTotalMs = sesion.DuracionTotalMs,
                        error = sesion.Error,
                        protocolos = sesion.Protocolos.ConvertAll(p => new
                        {
                            nombre = p.Nombre,
                            metodo = p.Metodo,
                            estado = p.Estado,
                            encontradas = p.Encontradas,
                            duracionMs = p.DuracionMs,
                            error = p.Error
                        }),
                        resultados = sesion.Resultados.ConvertAll(r => new
                        {
                            macFormateada = r.MacFormateada,
                            macNormalizada = r.MacNormalizada,
                            ip = r.Ip,
                            tieneIp = r.TieneIp,
                            vendor = r.Vendor,
                            modelo = r.Modelo,
                            hostname = r.Hostname,
                            descubiertaPor = r.DescubiertaPor,
                            yaAgregadaComoId = r.YaAgregadaComoId,
                            yaAgregadaComoNombre = r.YaAgregadaComoNombre
                        }),
                        totalEncontradas = sesion.Resultados.Count
                    };
                }
                WriteJsonResponse(ctx, 200, resp);
            }
            catch (Exception ex)
            {
                Log.Error("[DISCOVERY-API] Error consultando sesión", ex);
                WriteJsonResponse(ctx, 500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// POST /api/dashboard/discovery/add
        /// Body: { macNormalizada, ip, nombre, puerto, modelo, vendor }
        /// Agrega una impresora descubierta a la tabla `printers`. Si la MAC ya
        /// existe (dedup por MAC normalizada), devuelve 409 con el id/nombre
        /// existente. Si no, inserta con impresora_id nuevo.
        /// </summary>
        public void HandleDiscoveryAdd(HttpListenerContext ctx)
        {
            try
            {
                string body;
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = sr.ReadToEnd();
                if (string.IsNullOrEmpty(body))
                {
                    WriteJsonResponse(ctx, 400, new { error = "Body vacío" });
                    return;
                }

                var dto = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(body);
                string macNorm = PrinterServices.Services.Discovery.PrinterDiscoveryService.NormalizarMac(
                    (string)dto["macNormalizada"] ?? (string)dto["mac"]);
                string ip = (string)dto["ip"];
                string nombre = (string)dto["nombre"];
                string modelo = (string)dto["modelo"];
                string vendor = (string)dto["vendor"];
                int puerto = dto["puerto"] != null ? (int)dto["puerto"] : 9100;

                if (string.IsNullOrEmpty(macNorm))
                {
                    WriteJsonResponse(ctx, 400, new { error = "Se requiere MAC" });
                    return;
                }
                if (string.IsNullOrEmpty(nombre))
                {
                    WriteJsonResponse(ctx, 400, new { error = "Se requiere nombre visible" });
                    return;
                }

                // Chequear si ya existe por MAC.
                var yaExiste = _db.Query<Data.Models.PrinterEntity>(
                    "SELECT * FROM printers WHERE REPLACE(REPLACE(REPLACE(UPPER(mac_address), ':', ''), '-', ''), '.', '') = ? LIMIT 1",
                    macNorm);
                if (yaExiste != null && yaExiste.Count > 0)
                {
                    var e = yaExiste[0];
                    WriteJsonResponse(ctx, 409, new
                    {
                        error = "Impresora ya registrada",
                        impresoraId = e.ImpresoraId,
                        nombre = e.Nombre,
                        ip = e.Ip
                    });
                    return;
                }

                // Insert. Generamos un impresora_id nuevo.
                string nuevoId = "DISC_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
                var entity = new Data.Models.PrinterEntity
                {
                    ImpresoraId = nuevoId,
                    Nombre = nombre,
                    Ip = string.IsNullOrEmpty(ip) ? "" : ip,
                    Puerto = puerto > 0 ? puerto : 9100,
                    MacAddress = macNorm,
                    Modelo = modelo,
                    TipoConexion = "RED",
                    FechaRegistro = DateTime.Now.ToString("o")
                };
                _db.Insert(entity);
                Log.InfoFormat("[DISCOVERY-API] Impresora agregada: {0} ({1}) MAC={2} IP={3}",
                    nuevoId, nombre, macNorm, ip ?? "sin IP");

                WriteJsonResponse(ctx, 200, new
                {
                    exito = true,
                    impresoraId = nuevoId,
                    nombre = nombre,
                    mac = macNorm,
                    ip = ip
                });
            }
            catch (Exception ex)
            {
                Log.Error("[DISCOVERY-API] Error agregando impresora", ex);
                WriteJsonResponse(ctx, 500, new { error = ex.Message });
            }
        }

        private void WriteRawJsonResponse(HttpListenerContext ctx, int statusCode, string json)
        {
            try { var buffer = Encoding.UTF8.GetBytes(json); ctx.Response.ContentType = "application/json; charset=utf-8"; ctx.Response.StatusCode = statusCode; ctx.Response.ContentLength64 = buffer.Length; ctx.Response.OutputStream.Write(buffer, 0, buffer.Length); ctx.Response.Close(); }
            catch (HttpListenerException) { try { ctx.Response.Close(); } catch { } }
        }

        public void WriteJsonResponse(HttpListenerContext ctx, int statusCode, object data)
        {
            try { var json = JsonConvert.SerializeObject(data); var buffer = Encoding.UTF8.GetBytes(json); ctx.Response.ContentType = "application/json; charset=utf-8"; ctx.Response.StatusCode = statusCode; ctx.Response.ContentLength64 = buffer.Length; ctx.Response.OutputStream.Write(buffer, 0, buffer.Length); ctx.Response.Close(); }
            catch (HttpListenerException) { try { ctx.Response.Close(); } catch { } }
        }

        /// <summary>
        /// Calcula la duración en ms entre dos fechas ISO 8601. Retorna null si
        /// alguna falta o no se puede parsear, para que la UI muestre "-".
        /// </summary>
        private static long? CalcularDuracionMs(string fechaInicio, string fechaFin)
        {
            if (string.IsNullOrEmpty(fechaInicio) || string.IsNullOrEmpty(fechaFin)) return null;
            DateTime inicio, fin;
            if (!DateTime.TryParse(fechaInicio, null, System.Globalization.DateTimeStyles.RoundtripKind, out inicio)) return null;
            if (!DateTime.TryParse(fechaFin, null, System.Globalization.DateTimeStyles.RoundtripKind, out fin)) return null;
            double ms = (fin - inicio).TotalMilliseconds;
            if (ms < 0) return null;
            return (long)ms;
        }
    }
}
