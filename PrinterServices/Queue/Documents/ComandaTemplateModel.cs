using System.Collections.Generic;

namespace PrinterServices.Queue.Documents
{
    /// <summary>
    /// DTO para deserializar template_comanda.txt (JSON).
    /// Espejo del modelo del front — solo las propiedades necesarias para generar HTML.
    /// </summary>
    public class TemplateFieldDto
    {
        public string FieldName { get; set; }
        public string Label { get; set; }
        public string FontFamily { get; set; }
        public double FontSize { get; set; }
        public bool FontBold { get; set; }
        public bool FontItalic { get; set; }
        public string Alignment { get; set; }
        public bool Visible { get; set; }
        /// <summary>
        /// Contexto en que aplica: Todos, Salon, Delivery, Anulacion.
        /// Si es null o vacío se trata como "Todos".
        /// </summary>
        public string Context { get; set; }
    }

    public class ComandaTemplateDto
    {
        public int PaperWidth { get; set; }
        public List<TemplateFieldDto> Fields { get; set; }
    }
}
