using System;

namespace PrinterServices.Queue
{
    public enum PrintJobStatus
    {
        Pending,
        Printing,
        Done,
        Failed,
        Waiting
    }

    public class PrintJob
    {
        public string JobId { get; set; }
        public string ComandaId { get; set; }
        public string ImpresoraId { get; set; }
        public string ImpresoraIp { get; set; }
        public string ImpresoraNombre { get; set; }
        public string PrinterModel { get; set; }
        public string ModoImpresion { get; set; }
        public string Contenido { get; set; }
        public string ContenidoHtml { get; set; }
        public string DeviceIdOrigen { get; set; }
        public string IpOrigen { get; set; }
        public string TipoImpresion { get; set; }
        public int Copias { get; set; }
        public int Prioridad { get; set; }
        public int Puerto { get; set; }

        public PrintJobStatus Estado { get; set; }
        public int Reintentos { get; set; }
        public int MaxReintentos { get; set; }
        public string ErrorMensaje { get; set; }
        public DateTime FechaCreacion { get; set; }
        public DateTime? FechaImpresion { get; set; }

        public PrintJob()
        {
            JobId = Guid.NewGuid().ToString("N").Substring(0, 12);
            Estado = PrintJobStatus.Pending;
            Reintentos = 0;
            MaxReintentos = 3;
            Copias = 1;
            Prioridad = 2;
            Puerto = 9100;
            FechaCreacion = DateTime.Now;
        }

        public Data.Models.PrintJobEntity ToEntity()
        {
            return new Data.Models.PrintJobEntity
            {
                JobId = this.JobId,
                ComandaId = this.ComandaId,
                ImpresoraId = this.ImpresoraId,
                ImpresoraIp = this.ImpresoraIp,
                ImpresoraNombre = this.ImpresoraNombre,
                PrinterModel = this.PrinterModel,
                ModoImpresion = this.ModoImpresion,
                Contenido = this.Contenido,
                ContenidoHtml = this.ContenidoHtml,
                DeviceIdOrigen = this.DeviceIdOrigen,
                IpOrigen = this.IpOrigen,
                TipoImpresion = this.TipoImpresion,
                Copias = this.Copias,
                Prioridad = this.Prioridad,
                Estado = this.Estado.ToString().ToUpper(),
                Reintentos = this.Reintentos,
                MaxReintentos = this.MaxReintentos,
                ErrorMensaje = this.ErrorMensaje,
                FechaCreacion = this.FechaCreacion.ToString("o"),
                FechaImpresion = this.FechaImpresion?.ToString("o")
            };
        }

        public static PrintJob FromEntity(Data.Models.PrintJobEntity entity)
        {
            PrintJobStatus estado;
            if (!Enum.TryParse(entity.Estado, true, out estado))
            {
                estado = PrintJobStatus.Pending;
            }

            DateTime fechaCreacion;
            if (!DateTime.TryParse(entity.FechaCreacion, out fechaCreacion))
            {
                fechaCreacion = DateTime.Now;
            }

            DateTime? fechaImpresion = null;
            DateTime tempFecha;
            if (!string.IsNullOrEmpty(entity.FechaImpresion) && DateTime.TryParse(entity.FechaImpresion, out tempFecha))
            {
                fechaImpresion = tempFecha;
            }

            return new PrintJob
            {
                JobId = entity.JobId,
                ComandaId = entity.ComandaId,
                ImpresoraId = entity.ImpresoraId,
                ImpresoraIp = entity.ImpresoraIp,
                ImpresoraNombre = entity.ImpresoraNombre,
                PrinterModel = entity.PrinterModel,
                ModoImpresion = entity.ModoImpresion,
                Contenido = entity.Contenido,
                ContenidoHtml = entity.ContenidoHtml,
                DeviceIdOrigen = entity.DeviceIdOrigen,
                IpOrigen = entity.IpOrigen,
                TipoImpresion = entity.TipoImpresion,
                Copias = entity.Copias,
                Prioridad = entity.Prioridad,
                Estado = estado,
                Reintentos = entity.Reintentos,
                MaxReintentos = entity.MaxReintentos,
                ErrorMensaje = entity.ErrorMensaje,
                FechaCreacion = fechaCreacion,
                FechaImpresion = fechaImpresion,
                Puerto = 9100
            };
        }

    }
}
