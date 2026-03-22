using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using log4net;

namespace PrinterServices.Rendering
{
    /// <summary>
    /// Renderiza List&lt;RenderLine&gt; como Bitmap para impresión ESC/POS.
    ///
    /// RESPONSABILIDAD ÚNICA: Recibe líneas ya preparadas (con texto, fuente, tamaño,
    /// estilo y alineación) y las dibuja en un bitmap del ancho del papel.
    /// No parsea HTML, no reordena, no decide fuentes — eso lo hace ITipoDocumento.
    /// </summary>
    public static class ComandaBitmapRenderer
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ComandaBitmapRenderer));
        /// <summary>
        /// Renderiza líneas como Bitmap.
        /// </summary>
        /// <param name="lines">Líneas con texto + estilo completo</param>
        /// <param name="paperWidthMm">Ancho del papel en mm (80 o 58)</param>
        public static Bitmap RenderFromLines(List<RenderLine> lines, int paperWidthMm = 80)
        {
            if (lines == null || lines.Count == 0)
                return new Bitmap(1, 1);

            // Ancho en pixels a 203 DPI: 80mm=576px, 58mm=420px
            int bitmapWidth = (paperWidthMm <= 58) ? 420 : 576;

            // PASO 1: Medir alto total
            float totalHeight = 10; // margen superior

            using (var tempBmp = new Bitmap(1, 1))
            {
                tempBmp.SetResolution(203, 203);
                using (var g = Graphics.FromImage(tempBmp))
                {
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrEmpty(line.Text))
                        {
                            totalHeight += line.FontSize * 0.6f;
                            continue;
                        }
                        using (var font = CreateFont(line))
                        {
                            SizeF m = g.MeasureString(line.Text, font, bitmapWidth);
                            totalHeight += Math.Max(m.Height, line.FontSize + 2);
                        }
                    }
                }
            }
            totalHeight += 10; // margen inferior
            if (totalHeight < 1) totalHeight = 1;

            // PASO 2: Renderizar
            Bitmap bmp = new Bitmap(bitmapWidth, (int)Math.Ceiling(totalHeight));
            bmp.SetResolution(203, 203);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = SmoothingMode.HighQuality;

                float yPos = 10;

                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line.Text))
                    {
                        yPos += line.FontSize * 0.6f;
                        continue;
                    }

                    using (var font = CreateFont(line))
                    {
                        SizeF measured = g.MeasureString(line.Text, font, bitmapWidth);
                        float lineHeight = Math.Max(measured.Height, line.FontSize + 2);

                        // Calcular X según alineación
                        float xPos = 0;
                        if (line.Alignment == "Center")
                        {
                            xPos = Math.Max(0, (bitmapWidth - measured.Width) / 2);
                        }
                        else if (line.Alignment == "Right")
                        {
                            xPos = Math.Max(0, bitmapWidth - measured.Width);
                        }

                        g.DrawString(line.Text, font, Brushes.Black,
                            new RectangleF(xPos, yPos, bitmapWidth - xPos, lineHeight + 4));

                        yPos += lineHeight;
                    }
                }
            }

            return bmp;
        }

        /// <summary>
        /// Crea la fuente según la configuración del RenderLine.
        /// </summary>
        private static Font CreateFont(RenderLine line)
        {
            FontStyle style = FontStyle.Regular;
            if (line.Bold) style |= FontStyle.Bold;
            if (line.Italic) style |= FontStyle.Italic;

            string family = line.FontFamily ?? "Arial";

            try
            {
                var font = new Font(family, line.FontSize, style, GraphicsUnit.Point);
                // Verificar que se cargó la fuente solicitada (GDI+ puede sustituir silenciosamente)
                if (!font.Name.Equals(family, StringComparison.OrdinalIgnoreCase))
                {
                    Log.WarnFormat("[BITMAP] Fuente '{0}' no disponible, GDI+ usó '{1}'", family, font.Name);
                }
                return font;
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[BITMAP] Error creando fuente '{0}' {1}pt: {2}", family, line.FontSize, ex.Message);
                try { return new Font("Consolas", line.FontSize, style, GraphicsUnit.Point); }
                catch { return new Font("Arial", line.FontSize, style, GraphicsUnit.Point); }
            }
        }
    }
}
