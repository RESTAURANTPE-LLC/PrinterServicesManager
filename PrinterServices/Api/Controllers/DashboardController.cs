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

        public DashboardController(PrinterServiceDb db, PrintJobManager jobManager, ConfigManager config)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
            _config = config ?? throw new ArgumentNullException(nameof(config));
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
        /// RAZÓN: Modal de detalle en dashboard.
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
                    errorMensaje = job.ErrorMensaje,
                    printerResponse = job.PrinterResponse,
                    contenido = job.Contenido,
                    abreGaveta = job.AbreGaveta,
                    codigoCorte = job.CodigoCorte
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
                recentLatencies = recentLatencies
            };
        }
    }
}
