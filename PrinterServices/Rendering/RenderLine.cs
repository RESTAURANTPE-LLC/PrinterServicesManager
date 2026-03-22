namespace PrinterServices.Rendering
{
    /// <summary>
    /// Línea de renderizado lista para dibujar en el bitmap.
    /// Contiene texto + toda la información de estilo sin depender de HTML.
    /// Usado por ComandaBitmapRenderer.RenderFromLines().
    /// </summary>
    public class RenderLine
    {
        public string Text { get; set; }
        public string FontFamily { get; set; }
        public float FontSize { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public string Alignment { get; set; }

        public RenderLine() { }

        public RenderLine(string text, string fontFamily, float fontSize,
            bool bold, bool italic, string alignment)
        {
            Text = text;
            FontFamily = fontFamily;
            FontSize = fontSize;
            Bold = bold;
            Italic = italic;
            Alignment = alignment ?? "Left";
        }

        /// <summary>Línea vacía (espacio vertical)</summary>
        public static RenderLine Empty(float fontSize = 11)
        {
            return new RenderLine("", "Arial", fontSize, false, false, "Left");
        }
    }
}
