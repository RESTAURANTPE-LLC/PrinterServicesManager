using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Queue
{
    public class PrintJobManager
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrintJobManager));

        private readonly ConcurrentQueue<PrintJob> _queue;
        private readonly SemaphoreSlim _signal;
        private readonly PrinterServiceDb _db;
        private readonly object _statsLock = new object();

        private int _totalEnqueued;
        private int _totalProcessed;

        public int PendingCount { get { return _queue.Count; } }
        public int TotalEnqueued { get { return _totalEnqueued; } }
        public int TotalProcessed { get { return _totalProcessed; } }

        public PrintJobManager(PrinterServiceDb db)
        {
            _db = db;
            _queue = new ConcurrentQueue<PrintJob>();
            _signal = new SemaphoreSlim(0);
        }

        public string Enqueue(PrintJob job)
        {
            if (job == null) throw new ArgumentNullException("job");

            // Persistir en SQLite primero
            try
            {
                var entity = job.ToEntity();
                _db.Insert(entity);
                Log.DebugFormat("[QUEUE] Job {0} persistido en SQLite", job.JobId);
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error al persistir job en SQLite: " + ex.Message, ex);
            }

            _queue.Enqueue(job);
            Interlocked.Increment(ref _totalEnqueued);
            _signal.Release();

            Log.InfoFormat("[QUEUE] Job {0} encolado → impresora={1} ip={2} tipo={3}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId, job.ImpresoraIp, job.TipoImpresion);

            return job.JobId;
        }

        public async Task<PrintJob> DequeueAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct);

            PrintJob job;
            if (_queue.TryDequeue(out job))
            {
                Interlocked.Increment(ref _totalProcessed);
                return job;
            }

            return null;
        }

        public void Retry(PrintJob job)
        {
            if (job == null) return;

            job.Reintentos++;
            job.Estado = PrintJobStatus.Pending;
            job.ErrorMensaje = null;

            UpdateJobInDb(job);

            _queue.Enqueue(job);
            _signal.Release();

            Log.InfoFormat("[QUEUE] Job {0} re-encolado (reintento {1}/{2})",
                job.JobId, job.Reintentos, job.MaxReintentos);
        }

        public void MarkDone(PrintJob job)
        {
            job.Estado = PrintJobStatus.Done;
            job.FechaImpresion = DateTime.Now;
            UpdateJobInDb(job);

            Log.InfoFormat("[QUEUE] Job {0} completado → impresora={1}",
                job.JobId, job.ImpresoraNombre ?? job.ImpresoraId);
        }

        public void MarkFailed(PrintJob job, string error)
        {
            job.Estado = PrintJobStatus.Failed;
            job.ErrorMensaje = error;
            UpdateJobInDb(job);

            Log.WarnFormat("[QUEUE] Job {0} FALLIDO → {1}", job.JobId, error);
        }

        public void RecoverPending()
        {
            try
            {
                var pending = _db.Query<PrintJobEntity>(
                    "SELECT * FROM print_jobs WHERE estado IN ('PENDING','WAITING') ORDER BY prioridad ASC, fecha_creacion ASC");

                if (pending == null || pending.Count == 0)
                {
                    Log.Info("[QUEUE] No hay jobs pendientes para recuperar");
                    return;
                }

                foreach (var entity in pending)
                {
                    var job = PrintJob.FromEntity(entity);
                    _queue.Enqueue(job);
                    _signal.Release();
                }

                Log.InfoFormat("[QUEUE] Recuperados {0} jobs pendientes de SQLite", pending.Count);
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error al recuperar jobs pendientes: " + ex.Message, ex);
            }
        }

        public List<PrintJob> GetPendingJobs()
        {
            return _queue.ToArray().ToList();
        }

        public PrintJob GetJobById(string jobId)
        {
            // Primero buscar en cola
            var inQueue = _queue.ToArray().FirstOrDefault(j => j.JobId == jobId);
            if (inQueue != null) return inQueue;

            // Si no, buscar en SQLite
            try
            {
                var entity = _db.Query<PrintJobEntity>(
                    "SELECT * FROM print_jobs WHERE job_id = ?", jobId).FirstOrDefault();
                if (entity != null)
                {
                    return PrintJob.FromEntity(entity);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error buscando job " + jobId + ": " + ex.Message, ex);
            }

            return null;
        }

        private void UpdateJobInDb(PrintJob job)
        {
            try
            {
                var entity = job.ToEntity();
                _db.Update(entity);
            }
            catch (Exception ex)
            {
                Log.Error("[QUEUE] Error actualizando job " + job.JobId + " en SQLite: " + ex.Message, ex);
            }
        }
    }
}
