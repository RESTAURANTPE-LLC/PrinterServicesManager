using System.Collections.Generic;

namespace PrinterServices.Queue.Documents
{
    /// <summary>
    /// DTO para deserializar template_comanda.txt (JSON).
    /// Espejo del modelo del front — solo las propiedades necesarias para renderizar.
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

        /// <summary>
        /// Overrides de estilo por contexto. Key = "Salon", "Delivery", "Anulacion".
        /// Solo las propiedades no-null sobreescriben al estilo base.
        /// </summary>
        public Dictionary<string, ContextStyleOverrideDto> ContextStyles { get; set; }

        // ── Resolución de estilos efectivos por contexto ──

        /// <summary>Obtiene la etiqueta efectiva para un contexto (override o base)</summary>
        public string GetEffectiveLabel(string ctx)
        {
            var o = GetOverride(ctx);
            return (o != null && o.Label != null) ? o.Label : Label;
        }

        /// <summary>Obtiene la fuente efectiva para un contexto (override o base)</summary>
        public string GetEffectiveFontFamily(string ctx)
        {
            var o = GetOverride(ctx);
            return (o != null && o.FontFamily != null) ? o.FontFamily : FontFamily;
        }

        /// <summary>Obtiene el tamaño de fuente efectivo para un contexto (override o base)</summary>
        public double GetEffectiveFontSize(string ctx)
        {
            var o = GetOverride(ctx);
            return (o != null && o.FontSize.HasValue) ? o.FontSize.Value : FontSize;
        }

        /// <summary>Obtiene el bold efectivo para un contexto (override o base)</summary>
        public bool GetEffectiveFontBold(string ctx)
        {
            var o = GetOverride(ctx);
            return (o != null && o.FontBold.HasValue) ? o.FontBold.Value : FontBold;
        }

        /// <summary>Obtiene el italic efectivo para un contexto (override o base)</summary>
        public bool GetEffectiveFontItalic(string ctx)
        {
            var o = GetOverride(ctx);
            return (o != null && o.FontItalic.HasValue) ? o.FontItalic.Value : FontItalic;
        }

        /// <summary>Obtiene la alineación efectiva para un contexto (override o base)</summary>
        public string GetEffectiveAlignment(string ctx)
        {
            var o = GetOverride(ctx);
            return (o != null && o.Alignment != null) ? o.Alignment : Alignment;
        }

        /// <summary>Obtiene la visibilidad efectiva para un contexto (override o base)</summary>
        public bool GetEffectiveVisible(string ctx)
        {
            var o = GetOverride(ctx);
            return (o != null && o.Visible.HasValue) ? o.Visible.Value : Visible;
        }

        private ContextStyleOverrideDto GetOverride(string ctx)
        {
            if (string.IsNullOrEmpty(ctx) || ContextStyles == null) return null;
            ContextStyleOverrideDto ovr;
            return ContextStyles.TryGetValue(ctx, out ovr) ? ovr : null;
        }
    }

    /// <summary>
    /// Override de estilos para un contexto específico.
    /// Propiedades nullable: solo las que tienen valor sobreescriben al estilo base.
    /// </summary>
    public class ContextStyleOverrideDto
    {
        public string Label { get; set; }
        public string FontFamily { get; set; }
        public double? FontSize { get; set; }
        public bool? FontBold { get; set; }
        public bool? FontItalic { get; set; }
        public string Alignment { get; set; }
        public bool? Visible { get; set; }
    }

    public class ComandaTemplateDto
    {
        public int PaperWidth { get; set; }
        public List<TemplateFieldDto> Fields { get; set; }
    }
}
