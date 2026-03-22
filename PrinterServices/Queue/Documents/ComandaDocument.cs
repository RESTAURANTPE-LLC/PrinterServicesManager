using System.Text;

namespace PrinterServices.Queue.Documents
{
    /// <summary>
    /// Documento de impresión tipo Comanda.
    /// Recibe las propiedades individuales de la comanda y genera el HTML
    /// con la MISMA estructura que CreaTicket.crearCabeceraTicket() del servicio antiguo:
    ///
    ///   Línea 0: Operación        (h3 bold)
    ///   Línea 1: N° serie         (h3 bold)
    ///   Línea 2: ** AGREGADO **   (h3 bold)
    ///   Línea 3: AREA             (h4 = Lucida Console)
    ///   Línea 4: HORA             (h4)
    ///   Línea 5: MOZO             (h4)
    ///   Línea 6: SALA             (h4)
    ///   Línea 7: MESA (N Pax)    (h4)
    ///   Línea 8: CLIENTE          (h4)
    ///   (vacía)
    ///   Productos (del HTML original)
    ///
    /// PRINCIPIO SRP: Solo sabe armar la cabecera de comanda en orden CreaTicket.
    /// Los productos vienen de la cadenaHTML generada por ImpresionController.
    /// </summary>
    public class ComandaDocument : ITipoDocumento
    {
        // ─── Propiedades de cabecera (campos individuales) ───
        public string Operacion { get; set; }       // mesa_trazabilidadid
        public string Serie { get; set; }           // N° serie
        public string TipoImpresion { get; set; }   // AGREGADO, RE-IMPRESION, ANULACION
        public string Area { get; set; }            // Área de producción
        public string Hora { get; set; }            // Fecha/hora
        public string Mozo { get; set; }            // Nombre del mozo
        public string Salon { get; set; }           // Sala/salón
        public string Mesa { get; set; }            // Mesa
        public string CantPax { get; set; }         // Cantidad personas
        public string Cliente { get; set; }         // Nombre del cliente
        public string Modalidad { get; set; }       // Delivery, Venta Rápida, etc.

        // ─── Cuerpo: HTML de productos generado por ImpresionController ───
        public string ProductosHtml { get; set; }   // Parte de la cadenaHTML con productos

        /// <summary>
        /// Genera HTML completo con cabecera estilo CreaTicket + productos del HTML original.
        /// </summary>
        public string GenerarHtml()
        {
            var sb = new StringBuilder();

            // ═══════════════════════════════════════════════════════════════════
            // CABECERA: Orden CreaTicket.crearCabeceraTicket()
            // ═══════════════════════════════════════════════════════════════════

            // Línea 0: Operación
            if (!string.IsNullOrEmpty(Operacion))
            {
                sb.Append("<h2><b>Operación:").Append(Operacion).Append("</b></h2><br>");
            }

            // Línea 1: N° serie
            if (!string.IsNullOrEmpty(Serie))
            {
                sb.Append("<h2><b>N°: ").Append(Serie).Append("</b></h2><br>");
            }


            // Línea 2: MESA + PAX
            if (!string.IsNullOrEmpty(Mesa))
            {
                sb.Append("<h2><b>MESA: ").Append(Mesa);
                if (!string.IsNullOrEmpty(CantPax))
                {
                    sb.Append(" (").Append(CantPax).Append(" Pax)");
                }
                sb.Append("</b></h2><br>");
            }


            // Línea 3: AREA
            if (!string.IsNullOrEmpty(Area))
            {
                sb.Append("<h3>AREA: ").Append(Area).Append("</h3><br>");
            }

            // Línea 4: HORA
            if (!string.IsNullOrEmpty(Hora))
            {
                sb.Append("<h3>FECHA: ").Append(Hora).Append("</h3><br>");
            }

            // Línea 5: MOZO
            if (!string.IsNullOrEmpty(Mozo))
            {
                sb.Append("<h3>MOZO: ").Append(Mozo).Append("</h3><br>");
            }

            // Línea 6: SALA
            if (!string.IsNullOrEmpty(Salon))
            {
                sb.Append("<h3>SALA: ").Append(Salon).Append("</h3><br>");
            }



            // Línea 8: CLIENTE
            if (!string.IsNullOrEmpty(Cliente))
            {
                sb.Append("<h3>CLIENTE: ").Append(Cliente).Append("</h3><br>");
            }

            // Línea vacía antes de productos
            sb.Append("<br>");

            // ═══════════════════════════════════════════════════════════════════
            // CUERPO: Productos (HTML de ImpresionController, sin cabecera)
            // ═══════════════════════════════════════════════════════════════════
            if (!string.IsNullOrEmpty(ProductosHtml))
            {
                sb.Append(ProductosHtml);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Resuelve el tipo de impresión a texto legible (como CreaTicket).
        /// </summary>
        private static string ResolveTipoImpresion(string tipo)
        {
            if (string.IsNullOrEmpty(tipo)) return null;

            string upper = tipo.ToUpperInvariant();
            if (upper.Contains("AGREGADO") || upper == "1" || upper == "ADD")
                return "** AGREGADO **";
            if (upper.Contains("REIMPRESION") || upper.Contains("RE-IMPRESION") || upper == "2")
                return "** RE-IMPRESION **";
            if (upper.Contains("ANULA") || upper == "3")
                return "** ANULACION **";

            return null;
        }
    }
}
