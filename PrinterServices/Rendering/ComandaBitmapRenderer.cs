using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.RegularExpressions;

namespace PrinterServices.Rendering
{
    /// <summary>
    /// Renderiza cadenaHTML como Bitmap usando fuentes GDI estilo CreaTicket.
    ///
    /// RESPONSABILIDAD ÚNICA: Recibe HTML listo (ya con estructura correcta)
    /// y lo renderiza con las fuentes del servicio antiguo.
    /// NO reordena ni reestructura — eso es responsabilidad del ITipoDocumento
    /// que genera el HTML.
    ///
    /// Mapeo de tags HTML a fuentes CreaTicket:
    ///   h2 + b       → Arial Bold 17pt  (modalidad, títulos grandes)
    ///   h3 + b       → Arial Bold 13pt  (productos, combos, operación)
    ///   h3 + b + i   → Arial Bold Italic 13pt
    ///   h4            → Lucida Console 11pt (AREA, HORA, MOZO, SALA)
    ///   h4 + i       → Arial Italic 11pt (notas)
    ///   h4 + b       → Arial Bold 11pt  (secciones)
    ///   sin tags      → Lucida Console 11pt
    /// </summary>
    public static class ComandaBitmapRenderer
    {
        private class RenderLine
        {
            public string Text;
            public string FontFamily;
            public float FontSize;
            public FontStyle Style;

            public RenderLine(string text, string fontFamily, float fontSize, FontStyle style)
            {
                Text = text;
                FontFamily = fontFamily;
                FontSize = fontSize;
                Style = style;
            }
        }

        /// <summary>
        /// Renderiza HTML como Bitmap con fuentes GDI estilo CreaTicket.
        /// </summary>
        /// <param name="html">HTML listo para renderizar (generado por ITipoDocumento o ImpresionController)</param>
        /// <param name="tipotamanioFuente">"1"=Small, "2"=Medium (default), "3"=Big</param>
        public static Bitmap RenderAsBitmap(string html, string tipotamanioFuente = null)
        {
            if (string.IsNullOrEmpty(html))
                return new Bitmap(1, 1);

            // Tamaños de fuente (replica CreaTicket.sendPrint)
            float fsTitulo = 17, fsProducto = 13, fsNormal = 11, fsNota = 11;

            if (tipotamanioFuente == "3")
            { fsTitulo = 19; fsProducto = 14; fsNormal = 13; fsNota = 13; }
            else if (tipotamanioFuente == "1")
            { fsTitulo = 15; fsProducto = 12; fsNormal = 9; fsNota = 9; }

            var lines = ParseHtmlToLines(html, fsTitulo, fsProducto, fsNormal, fsNota);

            if (lines.Count == 0)
                return new Bitmap(1, 1);

            return RenderLines(lines);
        }

        private static List<RenderLine> ParseHtmlToLines(string html,
            float fsTitulo, float fsProducto, float fsNormal, float fsNota)
        {
            var lines = new List<RenderLine>();
            string[] segments = Regex.Split(html, @"<br\s*/?>", RegexOptions.IgnoreCase);

            foreach (string segment in segments)
            {
                string seg = segment.Trim();
                if (string.IsNullOrEmpty(seg))
                {
                    lines.Add(new RenderLine("", "Arial", fsNormal, FontStyle.Regular));
                    continue;
                }

                string text = Regex.Replace(seg, @"<[^>]+>", "").Trim();
                if (string.IsNullOrEmpty(text)) continue;

                bool hasH2 = Regex.IsMatch(seg, @"<h2\b", RegexOptions.IgnoreCase);
                bool hasH3 = Regex.IsMatch(seg, @"<h3\b", RegexOptions.IgnoreCase);
                bool hasH4 = Regex.IsMatch(seg, @"<h4\b", RegexOptions.IgnoreCase);
                bool hasBold = Regex.IsMatch(seg, @"<b>", RegexOptions.IgnoreCase);
                bool hasItalic = Regex.IsMatch(seg, @"<i>", RegexOptions.IgnoreCase);

                string fontFamily;
                float fontSize;
                FontStyle fontStyle;

                if (hasH2)
                {
                    fontFamily = "Arial"; fontSize = fsTitulo;
                    fontStyle = FontStyle.Bold;
                    if (hasItalic) fontStyle |= FontStyle.Italic;
                }
                else if (hasH3)
                {
                    fontFamily = "Arial"; fontSize = fsProducto;
                    fontStyle = FontStyle.Bold;
                    if (hasItalic) fontStyle |= FontStyle.Italic;
                }
                else if (hasH4)
                {
                    if (hasItalic)
                    { fontFamily = "Arial"; fontSize = fsNota; fontStyle = FontStyle.Italic; }
                    else if (hasBold)
                    { fontFamily = "Arial"; fontSize = fsNormal; fontStyle = FontStyle.Bold; }
                    else
                    { fontFamily = "Lucida Console"; fontSize = fsNormal; fontStyle = FontStyle.Regular; }
                }
                else
                {
                    fontFamily = "Lucida Console"; fontSize = fsNormal;
                    fontStyle = FontStyle.Regular;
                    if (hasBold) fontStyle |= FontStyle.Bold;
                    if (hasItalic) fontStyle |= FontStyle.Italic;
                }

                lines.Add(new RenderLine(text, fontFamily, fontSize, fontStyle));
            }

            return lines;
        }

        private static Bitmap RenderLines(List<RenderLine> lines)
        {
            int bitmapWidth = 576;
            float totalHeight = 10;

            using (var tempBmp = new Bitmap(1, 1))
            {
                tempBmp.SetResolution(203, 203);
                using (var g = Graphics.FromImage(tempBmp))
                {
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrEmpty(line.Text))
                        { totalHeight += line.FontSize * 0.6f; continue; }
                        using (var font = CreateFont(line.FontFamily, line.FontSize, line.Style))
                        {
                            SizeF m = g.MeasureString(line.Text, font, bitmapWidth);
                            totalHeight += Math.Max(m.Height, line.FontSize + 2);
                        }
                    }
                }
            }
            totalHeight += 10;
            if (totalHeight < 1) totalHeight = 1;

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
                    { yPos += line.FontSize * 0.6f; continue; }

                    using (var font = CreateFont(line.FontFamily, line.FontSize, line.Style))
                    {
                        SizeF m = g.MeasureString(line.Text, font, bitmapWidth);
                        g.DrawString(line.Text, font, Brushes.Black,
                            new RectangleF(0, yPos, bitmapWidth, m.Height + 4));
                        yPos += Math.Max(m.Height, line.FontSize + 2);
                    }
                }
            }

            return bmp;
        }

        private static Font CreateFont(string family, float size, FontStyle style)
        {
            try { return new Font(family, size, style); }
            catch
            {
                try { return new Font("Consolas", size, style); }
                catch { return new Font("Arial", size, style); }
            }
        }
    }
}
