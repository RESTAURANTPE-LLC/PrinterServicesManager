using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PrinterServices.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Text.RegularExpressions;

    public static class HtmlBitmapRenderer
    {
        private class HtmlRun
        {
            public string Text;
            public int FontSize;
            public FontStyle Style;

            public HtmlRun(string text, int fontSize, FontStyle style)
            {
                Text = text;
                FontSize = fontSize;
                Style = style;
            }
        }


        public static Bitmap RenderSimpleHtmlAsBitmapOLD2(string html)
        {
            int maxWidthPx = 584;

            // Reemplaza <br> con saltos de línea explícitos
            html = html.Replace("<br>", "\n").Replace("<BR>", "\n");

            // Divide en líneas
            string[] linesRaw = html.Split(new[] { '\n' }, StringSplitOptions.None);
            var lines = new List<List<HtmlRun>>();

            using (var tempBmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(tempBmp))
            {
                foreach (string lineRaw in linesRaw)
                {
                    var runsOriginal = ParseLine(lineRaw);
                    var runsProcessed = new List<HtmlRun>();

                    foreach (var run in runsOriginal)
                    {
                        var splitted = SplitRunToFitWidth(run, g, maxWidthPx);
                        runsProcessed.AddRange(splitted);
                    }

                    // Crear nuevas líneas si algún run fue partido en varias líneas
                    var currentLine = new List<HtmlRun>();
                    int currentLineWidth = 0;

                    foreach (var run in runsProcessed)
                    {
                        using (Font font = new Font("Arial", run.FontSize, run.Style))
                        {
                            SizeF size = g.MeasureString(run.Text, font);

                            if (currentLineWidth + size.Width > maxWidthPx)
                            {
                                lines.Add(currentLine);
                                currentLine = new List<HtmlRun>();
                                currentLineWidth = 0;
                            }

                            currentLine.Add(run);
                            currentLineWidth += (int)Math.Ceiling(size.Width);
                        }
                    }

                    if (currentLine.Count > 0)
                        lines.Add(currentLine);
                }
            }

            // Calcular tamaño total
            int totalHeight = 0;
            int maxWidth = 0;

            using (var tempBmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(tempBmp))
            {
                foreach (var line in lines)
                {
                    int lineWidth = 0;
                    int lineHeight = 0;

                    foreach (var run in line)
                    {
                        using (Font font = new Font("Arial", run.FontSize, run.Style))
                        {
                            SizeF size = g.MeasureString(run.Text, font);
                            lineWidth += (int)Math.Ceiling(size.Width);
                            lineHeight = Math.Max(lineHeight, (int)Math.Ceiling(size.Height));
                        }
                    }

                    totalHeight += lineHeight;
                    maxWidth = Math.Max(maxWidth, lineWidth);
                }
            }

            // Renderizar bitmap final
            Bitmap bmp = new Bitmap(maxWidth == 0 ? 1 : maxWidth, totalHeight == 0 ? 1 : totalHeight);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                int y = 0;

                foreach (var line in lines)
                {
                    int x = 0;
                    int lineHeight = 0;

                    foreach (var run in line)
                    {
                        using (Font font = new Font("Arial", run.FontSize, run.Style))
                        {
                            SizeF size = g.MeasureString(run.Text, font);
                            g.DrawString(run.Text, font, Brushes.Black, new PointF(x, y));
                            x += (int)Math.Ceiling(size.Width);
                            lineHeight = Math.Max(lineHeight, (int)Math.Ceiling(size.Height));
                        }
                    }

                    y += lineHeight;
                }
            }

            return bmp;
        }

        public static Bitmap RenderSimpleHtmlAsBitmap(string html)
        {
            int maxWidthPx = 584;
            html = html.Replace("<br>", "\n").Replace("<BR>", "\n");
            string[] linesRaw = html.Split(new[] { '\n' }, StringSplitOptions.None);
            var lines = new List<List<HtmlRun>>();

            using (var tempBmp = new Bitmap(1, 1))
            {
                tempBmp.SetResolution(192, 192);
                using (var g = Graphics.FromImage(tempBmp))
                {
                    foreach (string lineRaw in linesRaw)
                    {
                        var runsOriginal = ParseLine(lineRaw);
                        var runsProcessed = new List<HtmlRun>();

                        foreach (var run in runsOriginal)
                        {
                            var splitted = SplitRunToFitWidth(run, g, maxWidthPx);
                            runsProcessed.AddRange(splitted);
                        }

                        var currentLine = new List<HtmlRun>();
                        int currentLineWidth = 0;

                        foreach (var run in runsProcessed)
                        {
                            using (Font font = new Font("Arial", run.FontSize, run.Style))
                            {
                                SizeF size = g.MeasureString(run.Text, font);
                                if (currentLineWidth + size.Width > maxWidthPx)
                                {
                                    lines.Add(currentLine);
                                    currentLine = new List<HtmlRun>();
                                    currentLineWidth = 0;
                                }

                                currentLine.Add(run);
                                currentLineWidth += (int)Math.Ceiling(size.Width);
                            }
                        }

                        if (currentLine.Count > 0)
                            lines.Add(currentLine);
                    }
                }
            }

            int totalHeight = 0;
            int maxWidth = 0;

            using (var tempBmp = new Bitmap(1, 1))
            {
                tempBmp.SetResolution(192, 192);
                using (var g = Graphics.FromImage(tempBmp))
                {
                    foreach (var line in lines)
                    {
                        int lineWidth = 0;
                        int lineHeight = 0;

                        foreach (var run in line)
                        {
                            using (Font font = new Font("Arial", run.FontSize, run.Style))
                            {
                                SizeF size = g.MeasureString(run.Text, font);
                                lineWidth += (int)Math.Ceiling(size.Width);
                                lineHeight = Math.Max(lineHeight, (int)Math.Ceiling(size.Height));
                            }
                        }

                        totalHeight += lineHeight;
                        maxWidth = Math.Max(maxWidth, lineWidth);
                    }
                }
            }

            Bitmap bmp = new Bitmap(maxWidth == 0 ? 1 : maxWidth, totalHeight == 0 ? 1 : totalHeight);
            bmp.SetResolution(192, 192);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                int y = 0;

                foreach (var line in lines)
                {
                    int x = 0;
                    int lineHeight = 0;

                    foreach (var run in line)
                    {
                        using (Font font = new Font("Arial", run.FontSize, run.Style))
                        {
                            SizeF size = g.MeasureString(run.Text, font);
                            g.DrawString(run.Text, font, Brushes.Black, new PointF(x, y));
                            x += (int)Math.Ceiling(size.Width);
                            lineHeight = Math.Max(lineHeight, (int)Math.Ceiling(size.Height));
                        }
                    }

                    y += lineHeight;
                }
            }

            return bmp;
        }

        private static List<HtmlRun> SplitRunToFitWidth(HtmlRun run, Graphics g, int maxWidthPx)
        {
            var result = new List<HtmlRun>();
            string remainingText = run.Text;

            using (Font font = new Font("Arial", run.FontSize, run.Style))
            {
                while (!string.IsNullOrEmpty(remainingText))
                {
                    int cutLength = remainingText.Length;
                    SizeF size = g.MeasureString(remainingText, font);

                    if (size.Width <= maxWidthPx)
                    {
                        result.Add(new HtmlRun(remainingText, run.FontSize, run.Style));
                        break;
                    }

                    while (cutLength > 0)
                    {
                        string sub = remainingText.Substring(0, cutLength);
                        size = g.MeasureString(sub, font);

                        if (size.Width <= maxWidthPx)
                        {
                            result.Add(new HtmlRun(sub, run.FontSize, run.Style));
                            remainingText = remainingText.Substring(cutLength);
                            break;
                        }

                        cutLength--;
                    }

                    if (cutLength == 0)
                    {
                        cutLength = 1;
                        string sub = remainingText.Substring(0, cutLength);
                        result.Add(new HtmlRun(sub, run.FontSize, run.Style));
                        remainingText = remainingText.Substring(cutLength);
                    }
                }
            }

            return result;
        }

        private static List<HtmlRun> ParseLine(string html)
        {
            var runs = new List<HtmlRun>();

            if (string.IsNullOrEmpty(html))
                return runs;

            // Pilas para jerarquía
            var fontSizeStack = new Stack<int>();
            var styleStack = new Stack<FontStyle>();

            fontSizeStack.Push(12); // default
            styleStack.Push(FontStyle.Regular);

            int pos = 0;
            while (pos < html.Length)
            {
                if (html[pos] == '<')
                {
                    // Detectar tag
                    int endTag = html.IndexOf('>', pos);
                    if (endTag == -1)
                        break;

                    string tag = html.Substring(pos, endTag - pos + 1).ToLower();

                    // Procesar apertura/cierre
                    if (tag.StartsWith("<h"))
                    {
                        // Apertura h1-h4
                        int level = int.Parse(tag.Substring(2, 1));
                        int size = 12;
                        switch (level)
                        {
                            case 1: size = 20; break;
                            case 2: size = 15; break;
                            case 3: size = 12; break;
                            case 4: size = 10; break;
                            case 5: size = 5; break;
                        }
                        fontSizeStack.Push(size);
                    }
                    else if (tag.StartsWith("</h"))
                    {
                        // Cierre h1-h4
                        if (fontSizeStack.Count > 1)
                            fontSizeStack.Pop();
                    }
                    else if (tag.StartsWith("<b>"))
                    {
                        // Apertura bold
                        var current = styleStack.Peek();
                        styleStack.Push(current | FontStyle.Bold);
                    }
                    else if (tag.StartsWith("</b>"))
                    {
                        // Cierre bold
                        if (styleStack.Count > 1)
                            styleStack.Pop();
                    }
                    else if (tag.StartsWith("<i>"))
                    {
                        // Apertura italic
                        var current = styleStack.Peek();
                        styleStack.Push(current | FontStyle.Italic);
                    }
                    else if (tag.StartsWith("</i>"))
                    {
                        // Cierre italic
                        if (styleStack.Count > 1)
                            styleStack.Pop();
                    }

                    pos = endTag + 1;
                }
                else
                {
                    // Extraer texto plano hasta siguiente <
                    int nextTag = html.IndexOf('<', pos);
                    if (nextTag == -1)
                        nextTag = html.Length;

                    string text = html.Substring(pos, nextTag - pos);
                    runs.Add(new HtmlRun(text, fontSizeStack.Peek(), styleStack.Peek()));
                    pos = nextTag;
                }
            }

            return runs;

        }


        public static Bitmap RenderSimpleHtmlAsBitmap_old(string html)
        {
            // Reemplaza <br> con saltos de línea explícitos
            html = html.Replace("<br>", "\n").Replace("<BR>", "\n");

            // Divide en líneas
            string[] linesRaw = html.Split(new[] { '\n' }, StringSplitOptions.None);
            var lines = new List<List<HtmlRun>>();

            foreach (string lineRaw in linesRaw)
            {
                var runs = ParseLine(lineRaw);
                lines.Add(runs);
            }

            // Calcular tamaño total
            int totalHeight = 0;
            int maxWidth = 0;

            using (var tempBmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(tempBmp))
            {
                foreach (var line in lines)
                {
                    int lineWidth = 0;
                    int lineHeight = 0;

                    foreach (var run in line)
                    {
                        using (Font font = new Font("Arial", run.FontSize, run.Style))
                        {
                            SizeF size = g.MeasureString(run.Text, font);
                            lineWidth += (int)Math.Ceiling(size.Width);
                            lineHeight = Math.Max(lineHeight, (int)Math.Ceiling(size.Height));
                        }
                    }

                    totalHeight += lineHeight;
                    maxWidth = Math.Max(maxWidth, lineWidth);
                }
            }

            // Renderizar bitmap final
            Bitmap bmp = new Bitmap(maxWidth == 0 ? 1 : maxWidth, totalHeight == 0 ? 1 : totalHeight);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                int y = 0;

                foreach (var line in lines)
                {
                    int x = 0;
                    int lineHeight = 0;

                    foreach (var run in line)
                    {
                        using (Font font = new Font("Arial", run.FontSize, run.Style))
                        {
                            SizeF size = g.MeasureString(run.Text, font);
                            g.DrawString(run.Text, font, Brushes.Black, new PointF(x, y));
                            x += (int)Math.Ceiling(size.Width);
                            lineHeight = Math.Max(lineHeight, (int)Math.Ceiling(size.Height));
                        }
                    }

                    y += lineHeight;
                }
            }

            return bmp;
        }



}

}
