using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PrinterServices.Queue;

namespace PrinterServices.Api.Controllers
{
    public class JobController
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(JobController));

        private readonly PrintJobManager _jobManager;

        public JobController(PrintJobManager jobManager)
        {
            _jobManager = jobManager;
        }

        /// <summary>
        /// Fase 23: POST /api/jobs/status — Consulta bulk de estados de jobs.
        /// QuipuNetX envía una lista de jobIds y recibe el estado actual de cada uno.
        /// Usado para failover: cuando QuipuNetX se reinicia, consulta los estados actuales.
        /// Body: { "jobIds": ["714", "715", "716"] }
        /// Response: { "jobs": [{ "jobId": "714", "status": "DONE", ... }], "notFound": ["999"] }
        /// </summary>
        public ApiResult GetJobsStatus(string body)
        {
            try
            {
                if (string.IsNullOrEmpty(body))                        // Validar body no vacío
                {
                    return ApiResult.BadRequest("Body JSON es requerido"); // Error si no hay body
                }

                var parsed = JObject.Parse(body);                      // Parsear JSON
                var jobIdsToken = parsed["jobIds"];                     // Obtener array de jobIds

                if (jobIdsToken == null || jobIdsToken.Type != JTokenType.Array) // Validar array
                {
                    return ApiResult.BadRequest("Se requiere campo 'jobIds' como array"); // Error si no es array
                }

                var jobIds = jobIdsToken.ToObject<List<string>>();     // Convertir a lista de strings
                if (jobIds == null || jobIds.Count == 0)               // Validar lista no vacía
                {
                    return ApiResult.BadRequest("La lista de jobIds está vacía"); // Error si vacía
                }

                Log.InfoFormat("[JOB-API] Consulta bulk de {0} jobs", jobIds.Count); // Log de la consulta

                var jobs = _jobManager.GetJobsByIds(jobIds);           // Buscar jobs por IDs (memoria + BD)

                var foundIds = new HashSet<string>(jobs.Select(j => j.JobId)); // Set de IDs encontrados
                var notFound = jobIds.Where(id => !foundIds.Contains(id)).ToList(); // IDs no encontrados

                var response = new                                     // Construir respuesta
                {
                    jobs = jobs.Select(j => new                        // Lista de jobs con su estado
                    {
                        jobId = j.JobId,                               // ID del job
                        status = j.Estado.ToString().ToUpper(),        // Estado actual en mayúsculas
                        impresoraId = j.ImpresoraId,                   // ID de impresora asignada
                        impresoraNombre = j.ImpresoraNombre,           // Nombre de impresora
                        error = j.ErrorMensaje,                        // Mensaje de error (si hay)
                        reintentos = j.Reintentos,                     // Número de reintentos
                        fechaCreacion = j.FechaCreacion.ToString("o"), // Fecha de creación ISO8601
                        fechaImpresion = j.FechaImpresion?.ToString("o"), // Fecha de impresión (null si pendiente)
                        pedidoIds = j.PedidoIds                        // Lista de pedidos asociados
                    }),
                    notFound = notFound                                // IDs que no se encontraron
                };

                return ApiResult.Ok(JsonConvert.SerializeObject(response)); // Retornar JSON
            }
            catch (Exception ex)
            {
                Log.Error("[JOB-API] Error en consulta bulk de jobs", ex); // Log de error
                return ApiResult.Error("Error al consultar estados: " + ex.Message); // Error genérico
            }
        }

        public ApiResult GetJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return ApiResult.BadRequest("jobId es requerido");
            }

            var job = _jobManager.GetJobById(jobId);
            if (job == null)
            {
                return ApiResult.NotFound();
            }

            var response = new
            {
                jobId = job.JobId,
                comandaId = job.ComandaId,
                impresoraId = job.ImpresoraId,
                impresoraNombre = job.ImpresoraNombre,
                impresoraIp = job.ImpresoraIp,
                estado = job.Estado.ToString(),
                reintentos = job.Reintentos,
                maxReintentos = job.MaxReintentos,
                errorMensaje = job.ErrorMensaje,
                fechaCreacion = job.FechaCreacion.ToString("o"),
                fechaImpresion = job.FechaImpresion?.ToString("o")
            };

            return ApiResult.Ok(JsonConvert.SerializeObject(response));
        }

        public ApiResult GetPendingJobs()
        {
            var jobs = _jobManager.GetPendingJobs();

            var response = new
            {
                pendingCount = jobs.Count,
                totalEnqueued = _jobManager.TotalEnqueued,
                totalProcessed = _jobManager.TotalProcessed,
                jobs = jobs.Select(j => new
                {
                    jobId = j.JobId,
                    impresoraId = j.ImpresoraId,
                    impresoraNombre = j.ImpresoraNombre,
                    impresoraIp = j.ImpresoraIp,
                    estado = j.Estado.ToString(),
                    prioridad = j.Prioridad,
                    fechaCreacion = j.FechaCreacion.ToString("o")
                })
            };

            return ApiResult.Ok(JsonConvert.SerializeObject(response));
        }

        /// <summary>
        /// DELETE /api/job/{jobId} — Elimina un job y su historial de logs.
        /// RAZÓN: Permite al operador limpiar jobs obsoletos o erróneos desde el dashboard.
        /// </summary>
        public ApiResult DeleteJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return ApiResult.BadRequest("jobId es requerido"); // Validar parámetro
            }

            bool deleted = _jobManager.DeleteJob(jobId); // Eliminar de SQLite (print_jobs + print_log)

            if (!deleted)
            {
                return ApiResult.NotFound(); // No se encontró el job
            }

            Log.InfoFormat("[JOB] Job {0} eliminado por operador desde dashboard", jobId);

            var response = new
            {
                status = "DELETED",   // Confirmar eliminación
                jobId = jobId
            };

            return ApiResult.Ok(JsonConvert.SerializeObject(response));
        }

        public ApiResult RetryJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return ApiResult.BadRequest("jobId es requerido");
            }

            var job = _jobManager.GetJobById(jobId);
            if (job == null)
            {
                return ApiResult.NotFound();
            }

            // Si el job ya fue completado o está en proceso, retornar OK informativo
            // RAZÓN: El job pudo ser completado automáticamente (ExpirationLoop/StatusMonitor)
            // pero el callback DONE no llegó al cliente. Retornar currentStatus para que
            // el cliente actualice su UI y remueva el item de la lista visual.
            if (job.Estado == PrintJobStatus.Done
                || job.Estado == PrintJobStatus.Pending
                || job.Estado == PrintJobStatus.Printing)
            {
                string statusLabel = job.Estado == PrintJobStatus.Done ? "ALREADY_DONE" : "IN_PROGRESS";
                var infoResponse = new
                {
                    status = statusLabel,
                    jobId = job.JobId,
                    currentStatus = job.Estado.ToString().ToUpper()
                };
                Log.InfoFormat("[JOB] Retry solicitado para job {0} pero ya está en estado {1}",
                    jobId, job.Estado.ToString().ToUpper());
                return ApiResult.Ok(JsonConvert.SerializeObject(infoResponse));
            }

            // Aceptar retry de jobs FAILED, EXPIRED y WAITING
            // RAZÓN: FAILED = fallo de impresora (sin papel, tapa abierta)
            //         EXPIRED = superó tiempo máximo en WAITING
            //         WAITING = impresora offline, aún esperando — el usuario quiere forzar reintento
            // Fase 9: En modo cliente, el usuario puede reintentar jobs WAITING desde el modal
            if (job.Estado != PrintJobStatus.Failed
                && job.Estado != PrintJobStatus.Expired
                && job.Estado != PrintJobStatus.Waiting)
            {
                return ApiResult.BadRequest("Solo se pueden reintentar jobs FAILED, EXPIRED o WAITING");
            }

            job.Reintentos = 0;
            _jobManager.RetryManual(job); // Guard: permite salir de FAILED/EXPIRED, elimina hash previo

            var response = new
            {
                status = "RETRIED",
                jobId = job.JobId,
                newRetryCount = job.Reintentos
            };

            return ApiResult.Ok(JsonConvert.SerializeObject(response));
        }
    }
}
