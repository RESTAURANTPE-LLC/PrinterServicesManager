using System;
using System.Linq;
using log4net;
using Newtonsoft.Json;
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

            if (job.Estado != PrintJobStatus.Failed)
            {
                return ApiResult.BadRequest("Solo se pueden reintentar jobs FAILED");
            }

            job.Reintentos = 0;
            _jobManager.Retry(job);

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
