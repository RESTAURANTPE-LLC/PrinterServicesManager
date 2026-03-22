using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using log4net;
using Newtonsoft.Json;
using PrinterServices.Rendering;

namespace PrinterServices.Queue.Documents
{
    /// <summary>
    /// Documento de impresión tipo Comanda.
    /// Genera List&lt;RenderLine&gt; con la estructura configurada en el template.
    ///
    /// CABECERA: Desde las propiedades individuales, en el orden/estilo del template.
    /// PRODUCTOS: Parseados del HTML que genera ImpresionController (cadenaHTML).
    ///
    /// Si no hay template, usa un formato por defecto.
    ///
    /// CONTEXTOS: Detecta automáticamente si es Salon, Delivery o Anulacion
    /// y solo renderiza los campos que aplican al contexto actual.
    /// Replica las mismas condiciones de _getImpresionComandaMejorada.
    /// </summary>
    public class ComandaDocument : ITipoDocumento
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ComandaDocument));

        // ─── Propiedades de cabecera ───
        public string Operacion { get; set; }
        public string Serie { get; set; }
        public string TipoImpresion { get; set; }
        public string Area { get; set; }
        public string Hora { get; set; }
        public string Mozo { get; set; }
        public string MozoPedido { get; set; }
        public string Salon { get; set; }
        public string Mesa { get; set; }
        public string CantPax { get; set; }
        public string Cliente { get; set; }
        public string Empresa { get; set; }
        public string Comprobante { get; set; }
        public string Modalidad { get; set; }
        public string DeliveryId { get; set; }
        public string ModalidadEntrega { get; set; }
        public string HoraRecojo { get; set; }
        public string HoraEntrega { get; set; }
        public string Canal { get; set; }
        public string TiempoPreparacion { get; set; }
        public string Subcuenta { get; set; }
        public string Localizador { get; set; }
        public string NotaMesa { get; set; }
        public string PedidoId { get; set; }
        public string MotivoAnulacion { get; set; }
        public string TipoComanda { get; set; }

        // ─── Productos (HTML de ImpresionController) ───
        public string ProductosHtml { get; set; }

        /// <summary>
        /// Genera líneas de renderizado. Template si existe, default si no.
        /// </summary>
        public List<RenderLine> GenerarLineas()
        {
            string templatePath = GetTemplatePath();
            if (File.Exists(templatePath))
            {
                try
                {
                    string json = File.ReadAllText(templatePath);
                    var template = JsonConvert.DeserializeObject<ComandaTemplateDto>(json);
                    if (template != null && template.Fields != null && template.Fields.Count > 0)
                    {
                        Log.Debug("[COMANDA-DOC] Usando template: " + templatePath);
                        return GenerarLineasFromTemplate(template);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("[COMANDA-DOC] Error leyendo template, usando default: " + ex.Message);
                }
            }

            return GenerarLineasDefault();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Detección de contexto (replica lógica de _getImpresionComandaMejorada)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Detecta el contexto actual: "Anulacion", "Delivery" o "Salon".
        /// Usa las mismas condiciones que _getImpresionComandaMejorada.
        /// </summary>
        private string DetectarContexto()
        {
            // Anulación: TipoImpresion contiene "ANULA" o es "3"
            string tipoResuelto = ResolveTipoImpresion(TipoImpresion);
            if (tipoResuelto != null && tipoResuelto.Contains("ANULACION"))
                return "Anulacion";

            // Delivery: Modalidad es DELIVERY
            if (!string.IsNullOrEmpty(Modalidad)
                && Modalidad.Equals("DELIVERY", StringComparison.OrdinalIgnoreCase))
                return "Delivery";

            return "Salon";
        }

        /// <summary>
        /// Verifica si un campo aplica al contexto actual.
        /// Reglas HARDCODEADAS por FieldName — no depende de que el template tenga Context.
        /// Replica las mismas condiciones de _getImpresionComandaMejorada.
        /// </summary>
        private bool CampoAplicaAlContexto(string fieldName, string contexto)
        {
            switch (fieldName)
            {
                // ── Solo ANULACION ──
                case "MarcoAnulacion":
                case "MozoPedido":      // COMANDADO POR
                case "MotivoAnulacion":
                    return contexto == "Anulacion";

                // ── Solo DELIVERY ──
                case "DeliveryId":
                case "ModalidadEntrega":
                case "HoraRecojo":
                case "HoraEntrega":
                case "Canal":
                case "TiempoPreparacion":
                case "PedidoId":
                    return contexto == "Delivery";

                // ── Solo SALON ──
                case "Salon":
                case "Subcuenta":
                case "Localizador":
                    return contexto == "Salon";

                // ── TODOS los contextos ──
                default:
                    return true;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // Desde template (diseñador visual) — CON filtro de contexto
        // ═══════════════════════════════════════════════════════════════════

        private List<RenderLine> GenerarLineasFromTemplate(ComandaTemplateDto template)
        {
            var lines = new List<RenderLine>();
            string contexto = DetectarContexto();
            Log.DebugFormat("[COMANDA-DOC] Contexto detectado: {0}", contexto);

            foreach (var field in template.Fields)
            {
                if (!field.Visible) continue;

                // FILTRO DE CONTEXTO por FieldName — no depende del template
                if (!CampoAplicaAlContexto(field.FieldName, contexto))
                    continue;

                // Campos especiales
                if (field.FieldName == "ProductosHtml")
                {
                    lines.AddRange(ParseProductosHtml());
                    continue;
                }
                if (field.FieldName == "Separator")
                {
                    string sep = !string.IsNullOrEmpty(field.Label) ? field.Label : "─────────────────────";
                    lines.Add(new RenderLine(sep, field.FontFamily ?? "Arial",
                        (float)field.FontSize, field.FontBold, field.FontItalic, field.Alignment));
                    continue;
                }
                if (field.FieldName == "EmptyLine")
                {
                    lines.Add(RenderLine.Empty((float)field.FontSize));
                    continue;
                }
                if (field.FieldName == "MarcoAnulacion")
                {
                    // Solo renderizar si realmente es anulación
                    if (contexto == "Anulacion")
                        lines.AddRange(GenerarMarcoAnulacion(field));
                    continue;
                }

                // Obtener valor del campo
                string value = GetFieldValue(field.FieldName);
                if (string.IsNullOrEmpty(value)) continue;

                // Armar texto: Label + valor
                string text = string.IsNullOrEmpty(field.Label) ? value : field.Label + " " + value;

                lines.Add(new RenderLine(text,
                    field.FontFamily ?? "Arial",
                    (float)field.FontSize,
                    field.FontBold,
                    field.FontItalic,
                    field.Alignment));
            }

            return lines;
        }

        // ═══════════════════════════════════════════════════════════════════
        // Default (sin template)
        // ═══════════════════════════════════════════════════════════════════

        private List<RenderLine> GenerarLineasDefault()
        {
            var lines = new List<RenderLine>();

            AddIfNotEmpty(lines, "Operación:", Operacion, "Arial", 13, true, false, "Left");
            AddIfNotEmpty(lines, "N°:", Serie, "Arial", 13, true, false, "Left");

            string tipo = ResolveTipoImpresion(TipoImpresion);
            if (!string.IsNullOrEmpty(tipo))
                lines.Add(new RenderLine(tipo, "Arial", 13, true, false, "Center"));

            if (!string.IsNullOrEmpty(Mesa))
            {
                string mesaTexto = "MESA: " + Mesa;
                if (!string.IsNullOrEmpty(CantPax)) mesaTexto += " (" + CantPax + " Pax)";
                lines.Add(new RenderLine(mesaTexto, "Lucida Console", 11, false, false, "Left"));
            }

            AddIfNotEmpty(lines, "AREA:", Area, "Lucida Console", 11, false, false, "Left");
            AddIfNotEmpty(lines, "FECHA:", Hora, "Lucida Console", 11, false, false, "Left");
            AddIfNotEmpty(lines, "MOZO:", Mozo, "Lucida Console", 11, false, false, "Left");
            AddIfNotEmpty(lines, "SALA:", Salon, "Lucida Console", 11, false, false, "Left");
            AddIfNotEmpty(lines, "CLIENTE:", Cliente, "Lucida Console", 11, false, false, "Left");

            lines.Add(RenderLine.Empty());
            lines.AddRange(ParseProductosHtml());

            return lines;
        }

        // ═══════════════════════════════════════════════════════════════════
        // Productos: parsear HTML a RenderLines
        // ═══════════════════════════════════════════════════════════════════

        private List<RenderLine> ParseProductosHtml()
        {
            var lines = new List<RenderLine>();
            if (string.IsNullOrEmpty(ProductosHtml)) return lines;

            string[] segments = Regex.Split(ProductosHtml, @"<br\s*/?>", RegexOptions.IgnoreCase);

            foreach (string segment in segments)
            {
                string seg = segment.Trim();
                if (string.IsNullOrEmpty(seg)) continue;

                string text = Regex.Replace(seg, @"<[^>]+>", "").Trim();
                if (string.IsNullOrEmpty(text)) continue;

                bool hasH2 = Regex.IsMatch(seg, @"<h2\b", RegexOptions.IgnoreCase);
                bool hasH3 = Regex.IsMatch(seg, @"<h3\b", RegexOptions.IgnoreCase);
                bool hasH4 = Regex.IsMatch(seg, @"<h4\b", RegexOptions.IgnoreCase);
                bool hasBold = Regex.IsMatch(seg, @"<b>", RegexOptions.IgnoreCase);
                bool hasItalic = Regex.IsMatch(seg, @"<i>", RegexOptions.IgnoreCase);

                string fontFamily;
                float fontSize;
                bool bold, italic;

                if (hasH2)
                {
                    fontFamily = "Arial"; fontSize = 17; bold = true; italic = hasItalic;
                }
                else if (hasH3)
                {
                    fontFamily = "Arial"; fontSize = 13; bold = hasBold; italic = hasItalic;
                }
                else if (hasH4)
                {
                    if (hasItalic)
                    { fontFamily = "Arial"; fontSize = 11; bold = false; italic = true; }
                    else if (hasBold)
                    { fontFamily = "Arial"; fontSize = 11; bold = true; italic = false; }
                    else
                    { fontFamily = "Lucida Console"; fontSize = 11; bold = false; italic = false; }
                }
                else
                {
                    fontFamily = "Lucida Console"; fontSize = 11; bold = hasBold; italic = hasItalic;
                }

                lines.Add(new RenderLine(text, fontFamily, fontSize, bold, italic, "Left"));
            }

            return lines;
        }

        // ═══════════════════════════════════════════════════════════════════
        // Marco de anulación (****ANULADO****)
        // ═══════════════════════════════════════════════════════════════════

        private List<RenderLine> GenerarMarcoAnulacion(TemplateFieldDto field)
        {
            var lines = new List<RenderLine>();
            string ff = field.FontFamily ?? "Arial";
            float fs = (float)field.FontSize;

            lines.Add(new RenderLine("****************************", ff, fs, true, false, "Center"));
            lines.Add(new RenderLine("*       ANULADO            *", ff, fs, true, false, "Center"));
            if (!string.IsNullOrEmpty(Mesa))
                lines.Add(new RenderLine("*  MESA: " + Mesa + "  *", ff, fs, true, false, "Center"));
            lines.Add(new RenderLine("****************************", ff, fs, true, false, "Center"));

            return lines;
        }

        // ═══════════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════════

        private string GetFieldValue(string fieldName)
        {
            switch (fieldName)
            {
                case "Operacion": return Operacion;
                case "Serie": return Serie;
                case "TipoImpresion": return ResolveTipoImpresion(TipoImpresion);
                case "Mesa":
                    if (string.IsNullOrEmpty(Mesa)) return null;
                    return !string.IsNullOrEmpty(CantPax) ? Mesa + " (" + CantPax + " Pax)" : Mesa;
                case "CantPax": return CantPax;
                case "Area": return Area;
                case "Hora": return Hora;
                case "Mozo": return Mozo;
                case "MozoPedido": return MozoPedido;
                case "Salon": return Salon;
                case "Cliente": return Cliente;
                case "Empresa": return Empresa;
                case "Comprobante": return Comprobante;
                case "Modalidad":
                    // En el original, Modalidad nunca se muestra como texto directo.
                    // Solo se usa como condición: si es VENTA_RAPIDA → mostrar "VENTA RÁPIDA"
                    if (!string.IsNullOrEmpty(Modalidad)
                        && Modalidad.Equals("VENTA_RAPIDA", StringComparison.OrdinalIgnoreCase))
                        return "VENTA RÁPIDA";
                    return null;
                case "DeliveryId": return DeliveryId;
                case "ModalidadEntrega": return ModalidadEntrega;
                case "HoraRecojo": return HoraRecojo;
                case "HoraEntrega": return HoraEntrega;
                case "Canal": return Canal;
                case "TiempoPreparacion": return TiempoPreparacion;
                case "Subcuenta": return Subcuenta;
                case "Localizador": return Localizador;
                case "NotaMesa": return NotaMesa;
                case "PedidoId": return PedidoId;
                case "MotivoAnulacion": return MotivoAnulacion;
                default: return null;
            }
        }

        private static string ResolveTipoImpresion(string tipo)
        {
            if (string.IsNullOrEmpty(tipo)) return null;
            string upper = tipo.ToUpperInvariant();
            if (upper.Contains("AGREGADO") || upper == "1" || upper == "ADD") return "** AGREGADO **";
            if (upper.Contains("REIMPRESION") || upper.Contains("RE-IMPRESION") || upper == "2") return "** RE-IMPRESION **";
            if (upper.Contains("ANULA") || upper == "3") return "** ANULACION **";
            return null;
        }

        private static string ResolveModalidad(string modalidad)
        {
            if (string.IsNullOrEmpty(modalidad)) return null;
            if (modalidad == "0" || modalidad == "TODOS") return null;
            return modalidad;
        }

        private static void AddIfNotEmpty(List<RenderLine> lines, string label, string value,
            string fontFamily, float fontSize, bool bold, bool italic, string alignment)
        {
            if (string.IsNullOrEmpty(value)) return;
            string text = string.IsNullOrEmpty(label) ? value : label + " " + value;
            lines.Add(new RenderLine(text, fontFamily, fontSize, bold, italic, alignment));
        }

        private static string GetTemplatePath()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appDataPath, "QuipuNet", "template_comanda.txt");
        }
    }
}
