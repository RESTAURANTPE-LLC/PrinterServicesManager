using System;
using System.Collections.Generic;

namespace PrinterServices.Queue
{
    public enum PrintJobStatus
    {
        Pending,
        Printing,
        Done,
        Failed,
        Waiting,
        Expired,    // Fase 23: Job WAITING que superó el tiempo máximo de espera configurado
        Cancelled   // Fase 23: Job cancelado manualmente por el usuario o sistema
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
        public string IpOrigen { get; set; }                       // IP del cliente real que originó la impresión
        public string IpServidor { get; set; }                      // IP del servidor QuipuNet (siempre se notifica aquí)
        public string TipoImpresion { get; set; }
        public int Copias { get; set; }
        public int Prioridad { get; set; }
        public int Puerto { get; set; }
        public string LineasImprimirJson { get; set; }
        public string TamanioLetra { get; set; }
        public bool AbreGaveta { get; set; }
        public string TipoGeneracion { get; set; }
        public string QrData { get; set; }
        public string CodigoCorte { get; set; }
        public List<string> PedidoIds { get; set; }
        public string CashDrawerCode { get; set; }
        public string AreaImpresion { get; set; } // Área de producción (ej: "COCINA AUXILIAR", "BARRA")

        // ─── Fase 7B: Campos para ventas, encuestas y promociones ──────────────
        public bool FacturacionElectronica { get; set; }  // Flag: cadena contiene ##FE## marker para QR
        public string TamanioQr { get; set; }             // Tamaño QR configurado (ej: "3", "5")
        public string QrEncuesta { get; set; }            // Contenido del QR de encuesta (separado de QrData)
        public bool FormatoComandaMejorada { get; set; }  // POS 57: forzar HTML→Bitmap en BuildPayload
        public bool FormatoAntiguoServicio { get; set; } // Feature flag: renderizar comanda con fuentes GDI como el servicio antiguo
        public Documents.ITipoDocumento Documento { get; set; } // Documento tipado (ComandaDocument, VentaDocument, etc.) — genera HTML estilo CreaTicket

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
                IpOrigen = this.IpOrigen,                                     // IP del origen real (cliente o servidor)
                IpServidor = this.IpServidor,                                   // IP del servidor QuipuNet (siempre notificar)
                TipoImpresion = this.TipoImpresion,
                Copias = this.Copias,
                Prioridad = this.Prioridad,
                Estado = this.Estado.ToString().ToUpper(),
                Reintentos = this.Reintentos,
                MaxReintentos = this.MaxReintentos,
                ErrorMensaje = this.ErrorMensaje,
                FechaCreacion = this.FechaCreacion.ToString("o"),
                FechaImpresion = this.FechaImpresion?.ToString("o"),
                LineasImprimirJson = this.LineasImprimirJson,
                TamanioLetra = this.TamanioLetra,
                AbreGaveta = this.AbreGaveta ? 1 : 0,
                TipoGeneracion = this.TipoGeneracion,
                QrData = this.QrData,
                CodigoCorte = this.CodigoCorte,
                PedidoIds = this.PedidoIds != null ? string.Join(",", this.PedidoIds) : null,
                CashDrawerCode = this.CashDrawerCode,
                AreaImpresion = this.AreaImpresion,
                FacturacionElectronica = this.FacturacionElectronica ? 1 : 0,  // Fase 7B: persistir flag FE
                TamanioQr = this.TamanioQr,                                    // Fase 7B: persistir tamaño QR
                QrEncuesta = this.QrEncuesta,                                  // Fase 7B: persistir QR encuesta
                FormatoAntiguoServicio = this.FormatoAntiguoServicio ? 1 : 0   // Feature flag formato antiguo
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
                IpOrigen = entity.IpOrigen,                                    // IP del origen real (cliente o servidor)
                IpServidor = entity.IpServidor,                                 // IP del servidor QuipuNet (siempre notificar)
                TipoImpresion = entity.TipoImpresion,
                Copias = entity.Copias,
                Prioridad = entity.Prioridad,
                Estado = estado,
                Reintentos = entity.Reintentos,
                MaxReintentos = entity.MaxReintentos,
                ErrorMensaje = entity.ErrorMensaje,
                FechaCreacion = fechaCreacion,
                FechaImpresion = fechaImpresion,
                Puerto = 9100,
                LineasImprimirJson = entity.LineasImprimirJson,
                TamanioLetra = entity.TamanioLetra,
                AbreGaveta = entity.AbreGaveta == 1,
                TipoGeneracion = entity.TipoGeneracion,
                QrData = entity.QrData,
                CodigoCorte = entity.CodigoCorte,
                PedidoIds = !string.IsNullOrEmpty(entity.PedidoIds)
                    ? new List<string>(entity.PedidoIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    : new List<string>(),
                CashDrawerCode = entity.CashDrawerCode,
                AreaImpresion = entity.AreaImpresion,
                FacturacionElectronica = entity.FacturacionElectronica == 1,    // Fase 7B: restaurar flag FE
                TamanioQr = entity.TamanioQr,                                  // Fase 7B: restaurar tamaño QR
                QrEncuesta = entity.QrEncuesta,                                // Fase 7B: restaurar QR encuesta
                FormatoAntiguoServicio = entity.FormatoAntiguoServicio == 1     // Feature flag formato antiguo
            };
        }

    }
}
