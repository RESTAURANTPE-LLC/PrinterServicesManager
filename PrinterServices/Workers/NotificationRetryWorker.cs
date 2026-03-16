using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Notifications;

namespace PrinterServices.Workers
{
    /// <summary>
    /// Worker que reintenta enviar notificaciones de cambio de IP pendientes a QuipuNetX.
    /// Ejecuta cada N segundos (configurable) buscando notificaciones con estado PENDIENTE.
    /// </summary>
    public class NotificationRetryWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NotificationRetryWorker));

        private readonly PrinterServiceDb _db;
        private readonly ConfigManager _config;
        private readonly QuipuNetXNotifier _notifier;
        private readonly JobStatusCallbackNotifier _jobCallbackNotifier; // Fase 23: Notificador de callbacks de estado de jobs

        private Thread _workerThread;
        private CancellationTokenSource _cts;
        private bool _isRunning;

        public NotificationRetryWorker(PrinterServiceDb db, ConfigManager config)
        {
            _db = db;
            _config = config;
            _notifier = new QuipuNetXNotifier(db, config);
            _jobCallbackNotifier = new JobStatusCallbackNotifier(db, config); // Fase 23: Instanciar notificador de jobs
            _isRunning = false;
        }

        public void Start()
        {
            if (_isRunning)
            {
                Log.Warn("[RETRY-WORKER] Worker ya está ejecutándose");
                return;
            }

            Log.Info("[RETRY-WORKER] Iniciando NotificationRetryWorker...");

            _cts = new CancellationTokenSource();

            _workerThread = new Thread(() => WorkerLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "NotificationRetryWorker"
            };

            _isRunning = true;
            _workerThread.Start();

            Log.Info("[RETRY-WORKER] ✅ NotificationRetryWorker iniciado");
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                Log.Warn("[RETRY-WORKER] Worker ya está detenido");
                return;
            }

            Log.Info("[RETRY-WORKER] Deteniendo NotificationRetryWorker...");

            _cts?.Cancel();

            if (_workerThread != null && _workerThread.IsAlive)
            {
                bool joined = _workerThread.Join(TimeSpan.FromSeconds(5));
                if (!joined)
                {
                    Log.Warn("[RETRY-WORKER] Worker no se detuvo en 5s - abortando hilo");
                }
            }

            _isRunning = false;

            Log.Info("[RETRY-WORKER] ✅ NotificationRetryWorker detenido");
        }

        private void WorkerLoop(CancellationToken cancellationToken)
        {
            int intervalSeconds = _config.GetInt("NotificationRetryIntervalSeconds", 30);
            TimeSpan interval = TimeSpan.FromSeconds(intervalSeconds);

            Log.InfoFormat("[RETRY-WORKER] Intervalo de retry configurado: {0} segundos", intervalSeconds);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        ProcessPendingNotifications(cancellationToken).Wait();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[RETRY-WORKER] Error al procesar notificaciones pendientes", ex);
                    }

                    // Fase 23: Procesar callbacks de estado de jobs pendientes
                    try
                    {
                        ProcessPendingJobCallbacks(cancellationToken).Wait();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[RETRY-WORKER] Error al procesar callbacks de jobs pendientes", ex);
                    }

                    try
                    {
                        Task.Delay(interval, cancellationToken).Wait();
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Info("[RETRY-WORKER] Cancelación solicitada durante espera");
                        break;
                    }
                }

                Log.Info("[RETRY-WORKER] Worker loop finalizado (cancelación solicitada)");
            }
            catch (Exception ex)
            {
                Log.Error("[RETRY-WORKER] ❌ ERROR CRÍTICO en worker loop", ex);
            }
        }

        private async Task ProcessPendingNotifications(CancellationToken cancellationToken)
        {
            try
            {
                var pendingNotifications = _db.Query<IpChangeNotificationEntity>(
                    "SELECT * FROM notificacionescambiosip WHERE estado = 'PENDIENTE' ORDER BY fecha_creacion ASC");

                if (pendingNotifications == null || pendingNotifications.Count == 0)
                {
                    Log.Debug("[RETRY-WORKER] No hay notificaciones pendientes");
                    return;
                }

                Log.InfoFormat("[RETRY-WORKER] Procesando {0} notificación(es) pendiente(s)", pendingNotifications.Count);

                int sentCount = 0;
                int failedCount = 0;

                foreach (var notification in pendingNotifications)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Log.Info("[RETRY-WORKER] Cancelación solicitada - deteniendo procesamiento");
                        break;
                    }

                    try
                    {
                        bool sent = await _notifier.RetryNotificationAsync(notification);

                        if (sent)
                        {
                            sentCount++;
                        }
                        else
                        {
                            failedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorFormat("[RETRY-WORKER] Error al procesar notificación #{0}: {1}",
                            notification.Id, ex.Message);
                        failedCount++;
                    }
                }

                if (sentCount > 0 || failedCount > 0)
                {
                    Log.InfoFormat("[RETRY-WORKER] Ciclo completado: {0} enviada(s) ✅ | {1} fallida(s) ❌",
                        sentCount, failedCount);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RETRY-WORKER] Error crítico al procesar notificaciones pendientes", ex);
                throw;
            }
        }

        /// <summary>
        /// Fase 23: Procesa callbacks de estado de jobs pendientes de enviar a QuipuNetX.
        /// Mismo patrón que ProcessPendingNotifications pero para tabla job_status_callbacks.
        /// </summary>
        private async Task ProcessPendingJobCallbacks(CancellationToken cancellationToken)
        {
            try
            {
                var pendingCallbacks = _db.Query<JobStatusCallbackEntity>(                    // Buscar callbacks pendientes
                    "SELECT * FROM job_status_callbacks WHERE estado_envio = 'PENDIENTE' ORDER BY fecha_creacion ASC");

                if (pendingCallbacks == null || pendingCallbacks.Count == 0)                   // Si no hay pendientes, salir
                {
                    return;
                }

                Log.InfoFormat("[RETRY-WORKER] Procesando {0} callback(s) de jobs pendiente(s)", pendingCallbacks.Count);

                int sentCount = 0;                                                             // Contador de enviados
                int failedCount = 0;                                                           // Contador de fallidos

                foreach (var callback in pendingCallbacks)                                     // Iterar cada callback pendiente
                {
                    if (cancellationToken.IsCancellationRequested) break;                      // Cancelación solicitada

                    try
                    {
                        bool sent = await _jobCallbackNotifier.RetryCallbackAsync(callback);   // Reintentar envío
                        if (sent) sentCount++;                                                 // Incrementar contador éxito
                        else failedCount++;                                                    // Incrementar contador fallo
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorFormat("[RETRY-WORKER] Error al procesar callback #{0}: {1}",
                            callback.Id, ex.Message);                                          // Log de error individual
                        failedCount++;                                                         // Incrementar fallo
                    }
                }

                if (sentCount > 0 || failedCount > 0)                                         // Solo loguear si hubo actividad
                {
                    Log.InfoFormat("[RETRY-WORKER] Callbacks de jobs: {0} enviado(s) ✅ | {1} fallido(s) ❌",
                        sentCount, failedCount);                                               // Resumen del ciclo
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RETRY-WORKER] Error crítico al procesar callbacks de jobs", ex);   // Error fatal
                throw;
            }
        }

        public bool IsRunning()
        {
            return _isRunning;
        }
    }
}
