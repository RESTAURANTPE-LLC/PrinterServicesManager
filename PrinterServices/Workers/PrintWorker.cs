using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Drivers;
using PrinterServices.Queue;
using PrinterServices.Rendering;
using PrinterServices.Transport;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Notifications;
using PrinterServices.Core.Network;
using PrinterServices.Services.Network;

namespace PrinterServices.Workers
{
    public class PrintWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrintWorker));

        private readonly PrintJobManager _jobManager;
        private readonly PrinterServiceDb _db;
        private readonly CancellationTokenSource _cts;
        private readonly ILatencyMeasurement _latencyMeasurement;
        private readonly IDegradationDetector _degradationDetector;
        private readonly INetworkHealthChecker _networkHealthChecker;
        private readonly NetworkWatcher _networkWatcher;
        private readonly JobStatusCallbackNotifier _callbackNotifier; // Fase 23: Notificador HTTP de estado de jobs a QuipuNetX

        public PrintWorker(
            PrintJobManager jobManager,
            PrinterServiceDb db,
            ILatencyMeasurement latencyMeasurement,
            IDegradationDetector degradationDetector,
            INetworkHealthChecker networkHealthChecker,
            NetworkWatcher networkWatcher,
            JobStatusCallbackNotifier callbackNotifier) // Fase 23: Recibir notificador de callbacks
        {
            _jobManager = jobManager;
            _db = db;
            _latencyMeasurement = latencyMeasurement;
            _degradationDetector = degradationDetector;
            _networkHealthChecker = networkHealthChecker;
            _networkWatcher = networkWatcher;
            _callbackNotifier = callbackNotifier; // Fase 23: Guardar referencia al notificador
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            Task.Factory.StartNew(() => WorkLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // Segundo loop: revisar periódicamente jobs WAITING en BD y expirarlos si superaron el tiempo
            // RAZÓN: PrintWorker es responsable de las impresiones, incluyendo expirar jobs que
            // quedaron en WAITING cuando la impresora está offline y nunca vuelve.
            Task.Factory.StartNew(() => ExpirationLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Log.Info("[WORKER] PrintWorker iniciado (con loop de expiración)");
        }

        public void Stop()
        {
            _cts.Cancel();
            Log.Info("[WORKER] PrintWorker detenido");
        }

        private async Task WorkLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                PrintJob job = null;
                try
                {
                    job = await _jobManager.DequeueAsync(ct);
                    if (job == null) continue;

                    // Guard: Transición atómica PENDING → PRINTING
                    if (!_jobManager.MarkPrinting(job))
                    {
                        Log.WarnFormat("[WORKER] Job {0} omitido — no está PENDING (otro thread cambió estado)", job.JobId);
                        continue;
                    }

                    Log.InfoFormat("[WORKER] Procesando job {0} → {1} ({2})",
                        job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, job.ImpresoraIp);

                    await ProcessJobAsync(job, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("[WORKER] Error inesperado en work loop", ex);
                    if (job != null)
                    {
                        HandleFailure(job, ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Loop periódico que revisa jobs WAITING en BD y los expira si superaron el tiempo configurado.
        /// RAZÓN: PrintWorker es responsable de las impresiones. Cuando un job queda en WAITING
        /// (impresora offline) y la impresora no vuelve, este loop lo detecta y lo marca EXPIRED.
        /// Intervalo: cada 5 segundos revisa BD. Usa ExpirarImpresionDespuesDe de ConfigManager.
        /// </summary>
        private async Task ExpirationLoop(CancellationToken ct)
        {
            // Espera inicial: dejar que el servicio arranque completamente
            await Task.Delay(10000, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Consultar TODOS los jobs WAITING en BD (independiente de config de expiración)
                    // RAZÓN: Detectar si la impresora volvió online para re-encolar inmediatamente,
                    // además de expirar si superó el tiempo configurado.
                    // FIX: Si la desconexión fue breve (entre ciclos de StatusMonitor), StatusMonitor
                    // no detecta transición OFFLINE→ONLINE y el job queda atrapado en WAITING.
                    // Este loop lo detecta cada 5s consultando estado actual de la impresora en BD.
                    var waitingJobs = _db.Query<PrintJobEntity>(
                        "SELECT * FROM print_jobs WHERE estado = 'WAITING'");

                    if (waitingJobs != null && waitingJobs.Count > 0)
                    {
                        int expirarDespuesDe = ConfigManager.Instance.GetInt("ExpirarImpresionDespuesDe", 0);
                        int requeuedCount = 0;                             // Contador de re-encolados
                        int expiredCount = 0;                              // Contador de expirados

                        foreach (var entity in waitingJobs)                // Iterar cada job WAITING
                        {
                            var job = PrintJob.FromEntity(entity);         // Convertir entidad a PrintJob

                            // Guard: Verificar estado real en RAM antes de operar sobre dato de BD (stale)
                            var ramState = _jobManager.GetStateInMemory(job.JobId);
                            if (ramState.HasValue && PrintJobManager.IsTerminalState(ramState.Value))
                            {
                                continue; // Ya es DONE/FAILED/EXPIRED en RAM — BD está stale
                            }

                            // PRIORIDAD 1: Si la impresora volvió online y disponible → re-encolar
                            if (IsPrinterAvailableInDb(job.ImpresoraId))
                            {
                                if (_jobManager.ReEnqueue(job))
                                {
                                    LogPrint(job, "RE-ENQUEUED", "Impresora volvió online - reintentando impresión");
                                    requeuedCount++;
                                }
                                continue;
                            }

                            // PRIORIDAD 2: Impresora sigue offline → verificar expiración
                            if (expirarDespuesDe > 0)
                            {
                                double segundosTranscurridos = (DateTime.Now - job.FechaCreacion).TotalSeconds;

                                if (segundosTranscurridos > expirarDespuesDe)
                                {
                                    string expiredReason = string.Format(
                                        "Tiempo de impresión expirado ({0}s de {1}s permitidos). Impresora offline.",
                                        (int)segundosTranscurridos, expirarDespuesDe);

                                    Log.WarnFormat("[WORKER-EXPIRATION] Job {0} → impresora {1} EXPIRADO ({2}s > {3}s)",
                                        job.JobId, job.ImpresoraNombre ?? job.ImpresoraId,
                                        (int)segundosTranscurridos, expirarDespuesDe);

                                    _jobManager.MarkExpired(job, expiredReason);
                                    LogPrint(job, "EXPIRED", expiredReason);
                                    NotifyIfAvailable(n => n.NotifyPrintExpired(
                                        job.JobId, job.ComandaId, job.ImpresoraId,
                                        job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen, expiredReason));
                                    _callbackNotifier.NotifyStatusChangeFireAndForget(job, "EXPIRED", expiredReason);
                                    expiredCount++;
                                }
                            }
                        }

                        // Guard: Purga periódica de hashes antiguos (>24h)
                        _jobManager.PurgeOldHashes();

                        if (requeuedCount > 0)                             // Loguear si hubo re-encolados
                        {
                            Log.InfoFormat("[WORKER-RETRY] {0} job(s) WAITING re-encolado(s) (impresora volvió online)",
                                requeuedCount);
                        }
                        if (expiredCount > 0)                              // Loguear si hubo expirados
                        {
                            Log.WarnFormat("[WORKER-EXPIRATION] {0} job(s) WAITING expirado(s) (superaron {1}s)",
                                expiredCount, expirarDespuesDe);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;                                                 // Shutdown solicitado → salir
                }
                catch (Exception ex)
                {
                    Log.Error("[WORKER-EXPIRATION] Error en loop de expiración: " + ex.Message, ex);
                }

                // Esperar 2 segundos antes de la siguiente verificación
                // RAZÓN: Suficientemente frecuente para re-encolar jobs WAITING cuando la impresora
                // vuelve online (desconexiones breves entre ciclos de StatusMonitor).
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
            }

            Log.Info("[WORKER-EXPIRATION] Loop de expiración detenido");
        }

        private async Task ProcessJobAsync(PrintJob job, CancellationToken ct)
        {
            Log.InfoFormat("======[ JOB INICIO ]====== Job {0} → impresora={1} ip={2} estado={3} reintentos={4}/{5}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, job.ImpresoraIp,
                job.Estado, job.Reintentos, job.MaxReintentos);

            // Guard: Verificar si este job ya fue impreso (hash anti-duplicación)
            if (_jobManager.IsAlreadyPrinted(job.JobId))
            {
                _jobManager.MarkDone(job);
                LogPrint(job, "DONE", "Ya impreso (hash anti-duplicación)");
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "DONE");
                Log.WarnFormat("[GUARD] Job {0} ya impreso (hash encontrado) — omitido", job.JobId);
                return;
            }

            // ═══════════════════════════════════════════════════════════════════
            // Consultar impresora en BD UNA SOLA VEZ (tipo conexión + IP actualizada)
            // ═══════════════════════════════════════════════════════════════════
            Data.Models.PrinterEntity printerEntity = null;
            try
            {
                printerEntity = _db.Table<Data.Models.PrinterEntity>()
                    .FirstOrDefault(p => p.ImpresoraId == job.ImpresoraId);
            }
            catch (Exception ex)
            {
                Log.Warn("[WORKER] Error consultando impresora en BD: " + ex.Message);
            }

            // Si es USB, derivar a flujo USB específico
            if (printerEntity != null && printerEntity.TipoConexion == "USB"
                && !string.IsNullOrEmpty(printerEntity.UsbUniqueKey))
            {
                await ProcessUsbJobAsync(job, printerEntity.UsbUniqueKey, ct);
                return;
            }

            // ═══════════════════════════════════════════════════════════════════
            // FLUJO RED (comportamiento existente)
            // ═══════════════════════════════════════════════════════════════════

            // FASE 8: Verificar salud de red — INFORMATIVO, no bloqueante
            string networkStatus = _networkHealthChecker.GetCurrentNetworkStatus();
            bool networkChanged = (networkStatus == "changed");
            if (networkChanged)
            {
                Log.Warn($"[WORKER] Job {job.JobId} — Red cambió respecto a última impresión exitosa. Se intentará imprimir de todas formas.");
            }

            var cfg = ConfigManager.Instance;
            int connectTimeoutMs = cfg.GetInt("TcpConnectTimeoutMs", 3000);

            // ═══════════════════════════════════════════════════════════════════
            // RESOLUCIÓN MAC → IP: Usar IP de BD (ya consultada arriba)
            // ═══════════════════════════════════════════════════════════════════
            string effectiveIp = job.ImpresoraIp;
            int port = job.Puerto > 0 ? job.Puerto : cfg.GetInt("DefaultPrinterPort", 9100);

            if (printerEntity != null)
            {
                // Usar IP de la BD (actualizada por ArpScanWorker) en vez de la del job
                if (!string.IsNullOrEmpty(printerEntity.Ip) && printerEntity.Ip != effectiveIp)
                {
                    Log.InfoFormat("[WORKER] Job {0} — IP resuelta por BD: {1} → {2} (MAC: {3}, arpResolved={4})",
                        job.JobId, effectiveIp, printerEntity.Ip, printerEntity.MacAddress ?? "sin-mac",
                        printerEntity.IpResueltaPorArp);
                    effectiveIp = printerEntity.Ip;
                }

                // Actualizar puerto si la BD tiene uno diferente (más confiable)
                if (printerEntity.Puerto > 0)
                {
                    port = printerEntity.Puerto;
                }
            }

            // FASE 8: Iniciar medición de latencias (con IP efectiva, no la original)
            var timingBuilder = new LatencyTiming.Builder(
                jobId: job.JobId,
                impresoraId: job.ImpresoraId,
                impresoraIp: effectiveIp,
                enqueuedAt: job.FechaCreacion);
            DateTime startedAt = DateTime.Now;
            timingBuilder.Started(startedAt);

            // ═════════════════════════════════════════════════════════════════════
            // PORT LOCK: serializa TODO acceso TCP al port de esta impresora física
            // (pre-check DLE EOT + send del bitmap + wait post-print). Sin esto, el
            // StatusMonitor background (o otro job) puede abrir una 2a conexión a port
            // 9100 y las impresoras ESC/POS económicas mezclan los bytes de ambos
            // sockets en su buffer de entrada, truncando el ticket en cualquier punto.
            // ═════════════════════════════════════════════════════════════════════
            string printerResponseRaw = "";
            var portLock = await Core.Network.PrinterPortLock.AcquireAsync(effectiveIp, ct);
            try
            {

            // Pre-check: verificar si la impresora está online (usando IP efectiva resuelta por MAC)
            var printerStatus = await Monitoring.PrinterStatusChecker.CheckAsync(
                effectiveIp, port, connectTimeoutMs, ct);

            // ======[ PRINTER_RESPONSE ]====== Guardar respuesta DLE EOT raw para diagnóstico
            printerResponseRaw = printerStatus.RawStatus ?? (printerStatus.Online ? "TCP_OK_NO_DLE" : "OFFLINE:" + (printerStatus.ErrorMessage ?? "sin respuesta"));
            job.PrinterResponse = printerResponseRaw;
            try
            {
                _db.Execute("UPDATE print_jobs SET printer_response = ? WHERE job_id = ?",
                    printerResponseRaw, job.JobId);
            }
            catch (Exception ex)
            {
                Log.Debug("[WORKER] No se pudo guardar printer_response: " + ex.Message);
            }

            Log.InfoFormat("======[ PRE-CHECK ]====== Job {0} → {1} ({2}:{3}) | online={4} disponible={5} papel={6} tapa={7} raw={8}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, effectiveIp, port,
                printerStatus.Online, printerStatus.DisponibleParaImprimir,
                printerStatus.TienePapel, printerStatus.TapaAbierta, printerResponseRaw);

            if (!printerStatus.Online)
            {
                // RAZÓN: Si la impresora no es alcanzable Y la red cambió, enriquecer el mensaje
                // para que el diagnóstico sea más útil (el usuario sabe que puede ser la red)
                string offlineReason = networkChanged
                    ? "Impresora offline (posible causa: cambio de red detectado). Verifique que esté en la red correcta."
                    : "Impresora offline: " + (printerStatus.ErrorMessage ?? "sin conexión");

                Log.WarnFormat("======[ OFFLINE ]====== Job {0} → {1} ({2}) raw={3} error={4}",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, effectiveIp,
                    printerResponseRaw, printerStatus.ErrorMessage ?? "sin conexión");

                _jobManager.MarkWaiting(job, offlineReason);
                LogPrint(job, "WAITING", networkChanged ? "Impresora offline + red cambiada" : "Impresora offline");
                // Notificar WAITING via gRPC → servidores + cliente origen
                NotifyIfAvailable(n => n.NotifyPrintWaiting(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen, offlineReason));
                // Fase 23: Callback HTTP a QuipuNetX — notificar WAITING (fire-and-forget, no bloquea worker)
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "WAITING", offlineReason);
                return;
            }

            if (!printerStatus.TienePapel)
            {
                // RAZÓN: Sin papel es un ERROR, no espera. El Front debe mostrar el modal de fallo con el motivo.
                Log.WarnFormat("======[ SIN PAPEL ]====== Job {0} → {1} ({2}) raw={3}",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, effectiveIp, printerResponseRaw);

                _jobManager.MarkFailed(job, "Sin papel");                          // Marcar como FAILED con motivo
                LogPrint(job, "FAILED", "Sin papel");                              // Log con estado FAILED
                // Notificar FAILED por sin papel via gRPC → servidores + cliente origen
                NotifyIfAvailable(n => n.NotifyPrintFailed(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen,
                    "Sin papel", job.Reintentos));                                  // Motivo: Sin papel
                // Fase 23: Callback HTTP a QuipuNetX — notificar FAILED sin papel (fire-and-forget)
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "FAILED", "Sin papel");
                return;
            }

            // Verificar tapa abierta (confirmada por DLE EOT bit 2 del offline byte)
            if (printerStatus.TapaAbierta)
            {
                Log.WarnFormat("======[ TAPA ABIERTA ]====== Job {0} → {1} ({2}) raw={3}",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, effectiveIp, printerResponseRaw);

                _jobManager.MarkFailed(job, "Tapa abierta");
                LogPrint(job, "FAILED", "Tapa abierta");
                NotifyIfAvailable(n => n.NotifyPrintFailed(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen,
                    "Tapa abierta", job.Reintentos));
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "FAILED", "Tapa abierta");
                return;
            }

            // DLE no respondió pero TCP OK: la impresora está en red pero no procesó comandos de estado.
            // Puede ser busy transitorio (TM-T88VII) o tapa abierta real (TM-T20IIIL).
            // Intentar imprimir de todos modos — si la impresora tiene un problema real,
            // el envío TCP fallará y HandleFailure lo manejará con reintentos.
            if (!printerStatus.DisponibleParaImprimir && printerStatus.ErrorRecuperable)
            {
                Log.WarnFormat("======[ DLE NO RESPONDE ]====== Job {0} → {1} ({2}) raw={3} — intentando imprimir de todos modos",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, effectiveIp, printerResponseRaw);
            }

            Log.InfoFormat("======[ PRE-CHECK OK ]====== Job {0} → IMPRIMIENDO en {1} ({2}:{3}) raw={4}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, effectiveIp, port, printerResponseRaw);

            IPrinterDriver driver = DriverFactory.GetDriver(job.PrinterModel);

            // Construir payload ESC/POS (incluye ms estimados de impresión física cuando hay bitmap)
            BuiltPayload built = BuildPayload(driver, job);
            byte[] payload = built.Data;

            // Guard: Registrar hash ANTES de enviar bytes (protege contra crash post-impresión)
            string contentHash = PrintJobManager.CalculateContentHash(job);
            _jobManager.RegisterPrintHash(job, contentHash);

            // Enviar por cada copia (FASE 8: medir solo primera copia para timing)
            for (int copia = 0; copia < job.Copias; copia++)
            {
                if (job.Copias > 1)
                {
                    Log.DebugFormat("[WORKER] Job {0} — copia {1}/{2}", job.JobId, copia + 1, job.Copias);
                }

                bool sent = await SendWithRetryInstrumented(job, payload, effectiveIp, port, ct, copia == 0 ? timingBuilder : null, built.EstimatedWaitMs);
                if (!sent)
                {
                    // FASE 8: Registrar fallo en timing
                    if (copia == 0)
                    {
                        var failedTiming = timingBuilder.Completed(DateTime.Now, false, "Envío falló después de reintentos").Build();
                        _latencyMeasurement.RecordPrintLatency(failedTiming);
                    }
                    return; // Ya se manejó el failure (el finally libera el port lock)
                }
            }

            }
            finally
            {
                // Libera el port lock: el wait post-print dentro de SendWithRetryInstrumented
                // ya corrió en cada copia, así que es seguro permitir acceso concurrente a la
                // impresora (StatusMonitor u otros jobs) a partir de acá.
                portLock.Dispose();
            }

            // FASE 8: Completar timing exitoso
            DateTime completedAt = DateTime.Now;
            var successTiming = timingBuilder.Completed(completedAt, true).Build();
            _latencyMeasurement.RecordPrintLatency(successTiming);
            _latencyMeasurement.UpdatePrinterStats(job.ImpresoraId);

            // FASE 8: Detectar degradación de latencia
            string degradationStatus = _degradationDetector.DetectDegradation(job.ImpresoraId, successTiming);
            if (degradationStatus == "critical" || degradationStatus == "degraded")
            {
                Log.Warn($"[WORKER] Degradación detectada en {job.ImpresoraId}: {degradationStatus}");
            }

            // FASE 8: Registrar red actual como buena (impresión exitosa)
            _networkWatcher.RecordSuccessfulPrint();

            // Éxito
            _jobManager.MarkDone(job);
            string pedidoInfo = job.PedidoIds != null && job.PedidoIds.Count > 0
                ? " [pedidos: " + string.Join(",", job.PedidoIds) + "]"
                : "";
            Log.InfoFormat("======[ DONE ]====== Job {0} → {1} ({2}) raw={3}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, effectiveIp, printerResponseRaw);
            LogPrint(job, "DONE", "Impresión completada" + pedidoInfo);
            // Notificar éxito via gRPC → servidores + cliente origen
            NotifyIfAvailable(n => n.NotifyPrintSuccess(
                job.JobId, job.ComandaId, job.ImpresoraId,
                job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen));
            // Fase 23: Callback HTTP a QuipuNetX — notificar DONE (fire-and-forget, no bloquea worker)
            _callbackNotifier.NotifyStatusChangeFireAndForget(job, "DONE");
        }

        private BuiltPayload BuildPayload(IPrinterDriver driver, PrintJob job)
        {
            int estimatedWaitMs = 0;

            // ─── PRIORIDAD 0.5: DISEÑADOR VISUAL (ComandaDocument con prioridad absoluta) ───
            // Si el flag está activo, usa ComandaDocument para generar el ticket renderizado como bitmap.
            // Tiene PRIORIDAD sobre lineasimprimir (modo LINEAS) para forzar el uso del diseñador visual.
            // Si falla, lanza excepción → job queda FAILED (fail-fast, no fallback).
            if (job.UtilizarDisenadorComandas && job.Documento != null)
            {
                try
                {
                    // ITipoDocumento genera List<RenderLine> desde los campos enriquecidos
                    var renderLines = job.Documento.GenerarLineas();
                    if (renderLines != null && renderLines.Count > 0)
                    {
                        Log.InfoFormat("[WORKER] Job {0} — modo DISEÑADOR_VISUAL ({1}→{2} líneas→bitmap)",
                            job.JobId, job.Documento.GetType().Name, renderLines.Count);

                        var builder = new EscPosCommandBuilder(driver);
                        builder.Init();

                        // Renderizar líneas como bitmap con fuentes GDI (ComandaBitmapRenderer)
                        using (Bitmap bmp = Rendering.ComandaBitmapRenderer.RenderFromLines(renderLines))
                        using (Bitmap resized = BitmapResizer.ResizeIfNeeded(bmp, 576))
                        {
                            AddBitmapByEmulation(builder, resized);
                            estimatedWaitMs = PrintDurationEstimator.EstimateMs(resized);
                        }

                        if (job.AbreGaveta)
                        {
                            builder.OpenCashDrawer();
                        }

                        builder.Cut(CutType.Partial);
                        return new BuiltPayload { Data = builder.Build(), EstimatedWaitMs = estimatedWaitMs };
                    }
                    else
                    {
                        // Si GenerarLineas() retorna null o vacío, lanzar excepción
                        throw new InvalidOperationException(
                            string.Format("ComandaDocument.GenerarLineas() retornó null o vacío para job {0}", job.JobId));
                    }
                }
                catch (Exception ex)
                {
                    // Fail-fast: NO hay fallback a lineasimprimir cuando el diseñador está activo.
                    // El job debe fallar para alertar de bugs en ComandaDocument.
                    Log.ErrorFormat("[WORKER] Job {0} — DISEÑADOR_VISUAL FALLÓ: {1}", job.JobId, ex.Message);
                    Log.ErrorFormat("[WORKER] StackTrace: {0}", ex.StackTrace);
                    throw; // Relanzar excepción → job queda FAILED
                }
            }

            // ─── PRIORIDAD 1: LINEAS (modo estructurado JSON array) ───
            // Si tiene lineasimprimir, usar modo estructurado
            if (!string.IsNullOrEmpty(job.LineasImprimirJson))
            {
                var lineas = LineaParser.ParseFromJson(job.LineasImprimirJson);
                if (lineas.Count > 0)
                {
                    Log.DebugFormat("[WORKER] Job {0} — modo LINEAS ({1} líneas)", job.JobId, lineas.Count);
                    return new BuiltPayload { Data = LineaParser.BuildFromLineas(driver, lineas), EstimatedWaitMs = 0 };
                }
            }

            // ─── PRIORIDAD 2: Feature flag FORMATO ANTIGUO SERVICIO ───
            // Si está activo, renderiza la cadenaHTML como bitmap con fuentes GDI
            // (Arial Bold, Italic, Lucida Console) replicando el visual del servicio antiguo.
            // Usa cadenaHTML que tiene tags h2-h4/b/i generados por ImpresionController.
            // Fallback a cadena plana si cadenaHTML no viene.
            // Tiene prioridad sobre HTML→Bitmap (HtmlBitmapRenderer) y texto plano ESC/POS.
            if (job.FormatoAntiguoServicio && job.Documento != null)
            {
                // ITipoDocumento genera List<RenderLine> (cabecera + productos)
                // sin pasar por HTML — preserva fuente, tamaño, alineación del template
                var renderLines = job.Documento.GenerarLineas();
                if (renderLines != null && renderLines.Count > 0)
                {
                    Log.DebugFormat("[WORKER] Job {0} — modo FORMATO_ANTIGUO_SERVICIO ({1}→{2} líneas→GDI bitmap)",
                        job.JobId, job.Documento.GetType().Name, renderLines.Count);
                    var builder = new EscPosCommandBuilder(driver);
                    builder.Init();

                    using (Bitmap bmp = Rendering.ComandaBitmapRenderer.RenderFromLines(renderLines))
                    using (Bitmap resized = BitmapResizer.ResizeIfNeeded(bmp, 576))
                    {
                        AddBitmapByEmulation(builder, resized);
                        estimatedWaitMs = PrintDurationEstimator.EstimateMs(resized);
                    }

                    if (job.AbreGaveta)
                    {
                        builder.OpenCashDrawer();
                    }

                    builder.Cut(CutType.Partial);
                    return new BuiltPayload { Data = builder.Build(), EstimatedWaitMs = estimatedWaitMs };
                }
            }

            // Si tiene ContenidoHtml O FormatoComandaMejorada (POS 57) activo,
            // renderizar HTML → Bitmap → ESC/POS raster.
            // RAZÓN: POS 57 activa = QuipuNet generó CadenaHTML con tags HTML (<h2>, <b>, etc.)
            // y el Front la imprimiría como bitmap via HtmlBitmapRenderer. PS replica ese comportamiento.
            // Mismo fallback que PrintUtil.ProcesarModoEthernet: CadenaHTML ?? Cadena.
            // FIX: Precuentas siempre usan formato mejorado (HTML→Bitmap) aunque FormatoComandaMejorada
            // no esté persistido en BD (se pierde en retry). TipoImpresion="precuenta" fuerza el path.
            bool esPrecuenta = !string.IsNullOrEmpty(job.TipoImpresion)
                && job.TipoImpresion.ToLowerInvariant().Contains("precuenta");
            string htmlParaRenderizar = null;
            if (job.FormatoComandaMejorada || esPrecuenta)
            {
                htmlParaRenderizar = !string.IsNullOrEmpty(job.ContenidoHtml) ? job.ContenidoHtml : job.Contenido;
            }
            else if (!string.IsNullOrEmpty(job.ContenidoHtml))
            {
                htmlParaRenderizar = job.ContenidoHtml;
            }

            if (!string.IsNullOrEmpty(htmlParaRenderizar))
            {
                Log.DebugFormat("[WORKER] Job {0} — modo HTML→BITMAP (POS57={1})", job.JobId, job.FormatoComandaMejorada);
                var builder = new EscPosCommandBuilder(driver);
                builder.Init();

                using (Bitmap bmp = HtmlBitmapRenderer.RenderSimpleHtmlAsBitmap(htmlParaRenderizar))
                using (Bitmap resized = BitmapResizer.ResizeIfNeeded(bmp, 576))
                {
                    AddBitmapByEmulation(builder, resized);
                    estimatedWaitMs = PrintDurationEstimator.EstimateMs(resized);
                }

                if (job.AbreGaveta)
                {
                    builder.OpenCashDrawer();
                }

                builder.Cut(CutType.Partial);
                return new BuiltPayload { Data = builder.Build(), EstimatedWaitMs = estimatedWaitMs };
            }

            // Modo tradicional: cadena de texto plano
            Log.DebugFormat("[WORKER] Job {0} — modo CADENA", job.JobId);
            var textBuilder = new EscPosCommandBuilder(driver);
            textBuilder.Init();

            // Aplicar tamaño de letra si viene
            if (!string.IsNullOrEmpty(job.TamanioLetra))
            {
                textBuilder.SetLetterSize(job.TamanioLetra);
            }

            // ─── Fase 7B: Renderizar cadena con soporte para QR (FE y encuesta) ───
            // RAZÓN: imprimirVenta() en el Front busca ##FE## en cadena, split, imprime texto antes,
            // genera QR ESC/POS, imprime texto después. PS replica el mismo comportamiento.
            // imprimirEncuesta() usa BigWeightLetter + QR centrado. PS detecta via TipoImpresion o QrEncuesta.
            string contenido = job.Contenido ?? "";
            bool esEncuesta = !string.IsNullOrEmpty(job.QrEncuesta);                   // ¿Tiene QR de encuesta?
            bool esFEConMarker = job.FacturacionElectronica                            // ¿FE con ##FE## marker?
                && !string.IsNullOrEmpty(job.QrData)
                && contenido.Contains("##FE##");

            if (esEncuesta)
            {
                // ─── Encuesta: BigWeightLetter + QR centrado (replica imprimirEncuesta) ───
                textBuilder.SetFontSize(FontSize.DoubleWidthHeight);                   // BigWeightLetter = true
                textBuilder.SetAlignment(Alignment.Center);                            // Centrar texto
                textBuilder.Text(contenido);                                           // Texto de la encuesta
                textBuilder.SetAlignment(Alignment.Left);                              // Reset alineación
                textBuilder.SetFontSize(FontSize.Normal);                              // BigWeightLetter = false
                textBuilder.SetAlignment(Alignment.Center);                            // Centrar QR
                textBuilder.NewLine();                                                 // Salto antes del QR
                int qrSize = ParseQrSize(job.TamanioQr, 4);                           // Tamaño QR (default 4)
                textBuilder.PrintQrCode(job.QrEncuesta, qrSize, 1);                   // QR encuesta (ECC M)
                textBuilder.SetAlignment(Alignment.Left);                              // Reset alineación
            }
            else if (esFEConMarker)
            {
                // ─── Venta FE: split cadena en ##FE##, insertar QR (replica imprimirVenta) ───
                const string marker = "##FE##";
                int idx = contenido.IndexOf(marker, StringComparison.Ordinal);
                string textoAntes = contenido.Substring(0, idx);                       // Texto antes del QR
                string textoDespues = contenido.Substring(idx + marker.Length);         // Texto después del QR

                textBuilder.Text(textoAntes);                                          // Imprimir parte 1
                textBuilder.NewLine();                                                 // Salto antes del QR
                int qrSize = ParseQrSize(job.TamanioQr, 4);                           // Tamaño QR configurable
                textBuilder.SetAlignment(Alignment.Center);                            // Centrar QR
                textBuilder.PrintQrCode(job.QrData, qrSize, 1);                       // QR de facturación electrónica
                textBuilder.SetAlignment(Alignment.Left);                              // Reset alineación

                if (!string.IsNullOrEmpty(textoDespues))                               // Texto post-QR si existe
                {
                    textBuilder.Text(textoDespues);
                }
            }
            else
            {
                // ─── Texto simple (comandas, precuentas, etc.) ───
                if (!string.IsNullOrEmpty(contenido))
                {
                    textBuilder.Text(contenido);
                }
            }

            // Abrir gaveta si se solicitó
            if (job.AbreGaveta)
            {
                textBuilder.OpenCashDrawer();
            }

            // Corte
            textBuilder.Cut(CutType.Partial);

            return new BuiltPayload { Data = textBuilder.Build(), EstimatedWaitMs = 0 };
        }

        /// <summary>
        /// Resultado de BuildPayload: bytes ESC/POS + ms estimados que la impresora necesita
        /// para imprimir físicamente (solo > 0 cuando el payload incluye un bitmap).
        /// Se usa para esperar post-send y evitar que el siguiente job pise al actual.
        /// </summary>
        private struct BuiltPayload
        {
            public byte[] Data;
            public int EstimatedWaitMs;
        }

        /// <summary>
        /// Flujo de impresión para impresoras USB.
        /// Análogo a ProcessJobAsync pero usando UsbTransport en vez de TcpTransport.
        /// La identidad USB (VID+PID+Serial) es inmutable — si el usuario cambió de puerto,
        /// UsbTransport resuelve el DevicePath actual automáticamente.
        /// </summary>
        private async Task ProcessUsbJobAsync(PrintJob job, string usbUniqueKey, CancellationToken ct)
        {
            var cfg = ConfigManager.Instance;
            int checkTimeoutMs = cfg.GetInt("TcpConnectTimeoutMs", 3000);

            // FASE 8: Timing para USB
            var timingBuilder = new LatencyTiming.Builder(
                jobId: job.JobId,
                impresoraId: job.ImpresoraId,
                impresoraIp: "USB:" + usbUniqueKey,
                enqueuedAt: job.FechaCreacion);
            DateTime startedAt = DateTime.Now;
            timingBuilder.Started(startedAt);

            // Pre-check: verificar si la impresora USB está conectada y lista
            var printerStatus = await Monitoring.UsbPrinterStatusChecker.CheckAsync(
                usbUniqueKey, checkTimeoutMs, ct);

            if (!printerStatus.Online)
            {
                string offlineReason = "Impresora USB desconectada";
                Log.WarnFormat("[WORKER] Job {0} — impresora USB {1} ({2}) OFFLINE, moviendo a WAITING",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, usbUniqueKey);

                _jobManager.MarkWaiting(job, offlineReason);
                LogPrint(job, "WAITING", offlineReason);
                NotifyIfAvailable(n => n.NotifyPrintWaiting(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen, offlineReason));
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "WAITING", offlineReason);
                return;
            }

            if (!printerStatus.TienePapel)
            {
                Log.WarnFormat("[WORKER] Job {0} — impresora USB {1} SIN PAPEL, marcando FAILED",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId);
                _jobManager.MarkFailed(job, "Sin papel");
                LogPrint(job, "FAILED", "Sin papel");
                NotifyIfAvailable(n => n.NotifyPrintFailed(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen,
                    "Sin papel", job.Reintentos));
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "FAILED", "Sin papel");
                return;
            }

            if (printerStatus.TapaAbierta)
            {
                Log.WarnFormat("[WORKER] Job {0} — impresora USB {1} TAPA ABIERTA, marcando FAILED",
                    job.JobId, job.ImpresoraNombre ?? job.ImpresoraId);
                _jobManager.MarkFailed(job, "Tapa abierta");
                LogPrint(job, "FAILED", "Tapa abierta");
                NotifyIfAvailable(n => n.NotifyPrintFailed(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen,
                    "Tapa abierta", job.Reintentos));
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "FAILED", "Tapa abierta");
                return;
            }

            // Construir payload ESC/POS (idéntico para USB y RED)
            IPrinterDriver driver = DriverFactory.GetDriver(job.PrinterModel);
            Log.DebugFormat("[WORKER] Driver USB seleccionado: {0} para modelo {1}",
                driver.ModelName, job.PrinterModel ?? "null");

            BuiltPayload built = BuildPayload(driver, job);
            byte[] payload = built.Data;

            // Guard: Registrar hash ANTES de enviar bytes (protege contra crash post-impresión) — flujo USB
            string contentHash = PrintJobManager.CalculateContentHash(job);
            _jobManager.RegisterPrintHash(job, contentHash);

            // Enviar por cada copia
            for (int copia = 0; copia < job.Copias; copia++)
            {
                if (job.Copias > 1)
                {
                    Log.DebugFormat("[WORKER] Job USB {0} — copia {1}/{2}", job.JobId, copia + 1, job.Copias);
                }

                bool sent = await SendUsbWithRetry(job, payload, usbUniqueKey, ct,
                    copia == 0 ? timingBuilder : null, built.EstimatedWaitMs);
                if (!sent)
                {
                    if (copia == 0)
                    {
                        var failedTiming = timingBuilder.Completed(DateTime.Now, false,
                            "Envío USB falló después de reintentos").Build();
                        _latencyMeasurement.RecordPrintLatency(failedTiming);
                    }
                    return;
                }
            }

            // Completar timing exitoso
            DateTime completedAt = DateTime.Now;
            var successTiming = timingBuilder.Completed(completedAt, true).Build();
            _latencyMeasurement.RecordPrintLatency(successTiming);
            _latencyMeasurement.UpdatePrinterStats(job.ImpresoraId);

            // Detectar degradación
            string degradationStatus = _degradationDetector.DetectDegradation(job.ImpresoraId, successTiming);
            if (degradationStatus == "critical" || degradationStatus == "degraded")
            {
                Log.Warn($"[WORKER] Degradación detectada en USB {job.ImpresoraId}: {degradationStatus}");
            }

            // Éxito
            _jobManager.MarkDone(job);
            string pedidoInfo = job.PedidoIds != null && job.PedidoIds.Count > 0
                ? " [pedidos: " + string.Join(",", job.PedidoIds) + "]"
                : "";
            LogPrint(job, "DONE", "Impresión USB completada" + pedidoInfo);
            NotifyIfAvailable(n => n.NotifyPrintSuccess(
                job.JobId, job.ComandaId, job.ImpresoraId,
                job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen));
            _callbackNotifier.NotifyStatusChangeFireAndForget(job, "DONE");
        }

        /// <summary>
        /// Envía payload a impresora USB con reintentos exponenciales.
        /// Análogo a SendWithRetryInstrumented pero usando UsbTransport.
        /// estimatedWaitMs: ms a esperar tras el send para que la impresora termine de imprimir
        /// físicamente antes de liberar el worker (previene cruce de tickets con mucho negro).
        /// </summary>
        private async Task<bool> SendUsbWithRetry(PrintJob job, byte[] payload,
            string usbUniqueKey, CancellationToken ct, LatencyTiming.Builder timingBuilder,
            int estimatedWaitMs)
        {
            var cfg = ConfigManager.Instance;
            int maxRetries = cfg.GetInt("MaxRetries", 3);
            int retryBackoffBaseMs = cfg.GetInt("RetryBackoffBaseMs", 500);

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    int backoff = retryBackoffBaseMs * (1 << (attempt - 1));
                    Log.InfoFormat("[WORKER] Job USB {0} — retry {1}/{2}, esperando {3}ms",
                        job.JobId, attempt, maxRetries, backoff);
                    await Task.Delay(backoff, ct);
                }

                using (var transport = new UsbTransport(usbUniqueKey))
                {
                    try
                    {
                        DateTime beforeConnect = DateTime.Now;
                        await transport.ConnectAsync(ct);
                        if (timingBuilder != null)
                        {
                            timingBuilder.TcpConnected(DateTime.Now);
                        }

                        DateTime beforeSend = DateTime.Now;
                        await transport.SendAsync(payload, ct);
                        if (timingBuilder != null)
                        {
                            timingBuilder.DataSent(DateTime.Now, payload.Length);
                        }

                        transport.Disconnect();

                        Log.DebugFormat("[WORKER] Job USB {0} — datos enviados ({1} bytes) via {2}",
                            job.JobId, payload.Length, usbUniqueKey);

                        await WaitForPhysicalPrintAsync(job, estimatedWaitMs, "USB", ct);
                        return true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.WarnFormat("[WORKER] Job USB {0} — fallo intento {1}/{2}: {3}",
                            job.JobId, attempt + 1, maxRetries + 1, ex.Message);

                        if (attempt == maxRetries)
                        {
                            HandleFailure(job, "USB: " + ex.Message);
                            return false;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Envía payload a la impresora con reintentos exponenciales.
        /// RAZÓN: Recibe effectiveIp y port ya resueltos por MAC (no confiar en job.ImpresoraIp que puede estar desactualizada).
        /// estimatedWaitMs: ms a esperar tras el send para que la impresora termine de imprimir
        /// físicamente antes de liberar el worker (previene cruce de tickets con mucho negro).
        /// </summary>
        private async Task<bool> SendWithRetryInstrumented(PrintJob job, byte[] payload, string effectiveIp, int port, CancellationToken ct, LatencyTiming.Builder timingBuilder, int estimatedWaitMs)
        {
            var cfg = ConfigManager.Instance;
            int maxRetries = cfg.GetInt("MaxRetries", 3);
            int retryBackoffBaseMs = cfg.GetInt("RetryBackoffBaseMs", 500);

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    int backoff = retryBackoffBaseMs * (1 << (attempt - 1)); // exponential: 500, 1000, 2000
                    Log.InfoFormat("[WORKER] Job {0} — retry {1}/{2}, esperando {3}ms",
                        job.JobId, attempt, maxRetries, backoff);
                    await Task.Delay(backoff, ct);
                }

                using (var transport = new TcpTransport(effectiveIp, port))
                {
                    try
                    {
                        // FASE 8: Medir TCP connect
                        DateTime beforeConnect = DateTime.Now;
                        await transport.ConnectAsync(ct);
                        if (timingBuilder != null)
                        {
                            timingBuilder.TcpConnected(DateTime.Now);
                        }

                        // FASE 8: Medir envío de datos
                        DateTime beforeSend = DateTime.Now;
                        await transport.SendAsync(payload, ct);
                        if (timingBuilder != null)
                        {
                            timingBuilder.DataSent(DateTime.Now, payload.Length);
                        }

                        transport.Disconnect();

                        Log.DebugFormat("[WORKER] Job {0} — datos enviados ({1} bytes)", job.JobId, payload.Length);

                        await WaitForPhysicalPrintAsync(job, estimatedWaitMs, "TCP", ct);
                        return true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.WarnFormat("[WORKER] Job {0} — fallo intento {1}/{2}: {3}",
                            job.JobId, attempt + 1, maxRetries + 1, ex.Message);

                        if (attempt == maxRetries)
                        {
                            HandleFailure(job, ex.Message);
                            return false;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Espera proporcional al negro del bitmap para que la impresora térmica termine de
        /// imprimir físicamente antes de liberar al worker y permitir el siguiente job.
        /// RAZÓN: SendAsync devuelve cuando los bytes salen por TCP/USB, no cuando el papel
        /// salió. Si el siguiente job arranca antes, su ESC @ (Init) reinicia la impresora
        /// y corta el ticket anterior a la mitad, cruzándolo con el nuevo.
        /// El ms estimado viene de PrintDurationEstimator (height + densidad de negro por fila),
        /// clampeado por PostPrintWaitMin/MaxMs. Se respeta CancellationToken para shutdown.
        /// </summary>
        private async Task WaitForPhysicalPrintAsync(PrintJob job, int estimatedWaitMs, string transportLabel, CancellationToken ct)
        {
            if (estimatedWaitMs <= 0) return;

            var cfg = ConfigManager.Instance;
            if (!cfg.GetBool("PostPrintWaitEnabled", true)) return;

            Log.DebugFormat("[WORKER] Job {0} — wait post-send {1}ms ({2}) para que impresora termine físicamente",
                job.JobId, estimatedWaitMs, transportLabel);

            try { await Task.Delay(estimatedWaitMs, ct); }
            catch (OperationCanceledException) { /* shutdown: salir sin loguear como error */ }
        }

        private void HandleFailure(PrintJob job, string error)
        {
            if (job.Reintentos < job.MaxReintentos)
            {
                _jobManager.Retry(job);
                LogPrint(job, "RETRY", error);
                // Notificar REINTENTO via gRPC → solo servidores (informativo, el job aún no terminó)
                NotifyIfAvailable(n => n.NotifyPrintRetry(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen, job.Reintentos + 1));
            }
            else
            {
                _jobManager.MarkFailed(job, error);
                LogPrint(job, "FAILED", error);
                // Notificar FALLIDA via gRPC → servidores + cliente origen (todos los reintentos agotados)
                NotifyIfAvailable(n => n.NotifyPrintFailed(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen,
                    error, job.Reintentos));
                // Fase 23: Callback HTTP a QuipuNetX — notificar FAILED (fire-and-forget, no bloquea worker)
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "FAILED", error);
            }
        }

        /// <summary>
        /// Helper para enviar notificaciones de forma segura.
        /// Si NotificationManager aún no fue inicializado (ej: durante arranque),
        /// captura InvalidOperationException y la ignora silenciosamente.
        /// Cualquier otro error se loguea pero NO detiene el flujo de impresión.
        /// Esto desacopla el worker de gRPC: si las notificaciones fallan, la impresión sigue.
        /// </summary>
        private void NotifyIfAvailable(Action<NotificationManager> action)
        {
            try
            {
                action(NotificationManager.Instance); // Ejecuta la acción de notificación
            }
            catch (InvalidOperationException)
            {
                // NotificationManager aún no inicializado (GetInstance no fue llamado) — ignorar
            }
            catch (Exception ex)
            {
                // Error al notificar — loguear pero NO relanzar, la impresión no debe fallar por esto
                Log.Warn("[WORKER] Error al notificar: " + ex.Message);
            }
        }

        /// <summary>
        /// Parsea el tamaño de QR desde string a int.
        /// RAZÓN: QuipuNet envía impresora_tamanioqr como string (ej: "3", "5").
        /// El Front usa configurarTamanoQr() que mapea estos valores a moduleSize del ESC/POS.
        /// </summary>
        /// <summary>
        /// Agrega bitmap al builder usando el modo configurado en BitmapEmulacion:
        /// "escpos" → GS v 0 (raster estándar Epson)
        /// "escasterisc" → ESC * modo 33 (bit image por bandas, compatible CUSTOM/POS)
        /// </summary>
        private static void AddBitmapByEmulation(EscPosCommandBuilder builder, Bitmap bmp)
        {
            string emulacion = ConfigManager.Instance.GetString("BitmapEmulacion", "escpos").ToLowerInvariant().Trim();
            if (emulacion == "escasterisc")
            {
                builder.AddBitmapEscAsterisk(bmp);
            }
            else
            {
                builder.AddBitmapFromImage(bmp);
            }
        }

        private static int ParseQrSize(string tamanioQr, int defaultSize)
        {
            if (string.IsNullOrEmpty(tamanioQr)) return defaultSize;         // Sin config → default
            int size;
            if (int.TryParse(tamanioQr, out size) && size >= 1 && size <= 16)
            {
                return size;                                                  // Valor válido 1-16
            }
            return defaultSize;                                               // Valor inválido → default
        }

        /// <summary>
        /// Verifica si una impresora está online y disponible para imprimir consultando la BD.
        /// RAZÓN: Permite re-encolar jobs WAITING cuando la impresora se reconecta,
        /// incluso si StatusMonitor no detectó la transición OFFLINE→ONLINE
        /// (desconexión breve entre ciclos de monitoreo).
        /// </summary>
        private bool IsPrinterAvailableInDb(string impresoraId)
        {
            try
            {
                var results = _db.Query<PrinterEntity>(
                    "SELECT * FROM printers WHERE impresora_id = ? AND estado_online = 1 AND disponible_para_imprimir = 1",
                    impresoraId);
                return results != null && results.Count > 0;
            }
            catch (Exception ex)
            {
                Log.Debug("[WORKER] Error consultando disponibilidad de impresora " + impresoraId + ": " + ex.Message);
                return false;
            }
        }

        private void LogPrint(PrintJob job, string estado, string mensaje)
        {
            try
            {
                var log = new PrintLogEntity
                {
                    JobId = job.JobId,
                    ImpresoraId = job.ImpresoraId,
                    ImpresoraNombre = job.ImpresoraNombre,
                    ImpresoraIp = job.ImpresoraIp,
                    Estado = estado,
                    Mensaje = mensaje,
                    Reintentos = job.Reintentos,
                    Fecha = DateTime.Now.ToString("o"),
                    DeviceIdOrigen = job.DeviceIdOrigen,
                    AreaImpresion = job.AreaImpresion // Área de producción para trazabilidad
                };
                _db.Insert(log);
            }
            catch (Exception ex)
            {
                Log.Error("[WORKER] Error al registrar log de impresión: " + ex.Message, ex);
            }
        }
    }
}
