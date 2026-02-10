using System;
using System.Collections.Generic;
using log4net;
using Newtonsoft.Json.Linq;

namespace PrinterServices.Drivers
{
    public class LineaData
    {
        public string Texto { get; set; }
        public string Estilo { get; set; }
        public string Fuente { get; set; }
        public int LinesTotal { get; set; }
        public int TamanioSaltoLinea { get; set; }
        public int Tamanio { get; set; }
        public string EsCodigoBarras { get; set; }

        public bool IsBold
        {
            get
            {
                return !string.IsNullOrEmpty(Estilo) &&
                       Estilo.IndexOf("BOLD", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public bool IsCenter
        {
            get
            {
                return !string.IsNullOrEmpty(Estilo) &&
                       Estilo.IndexOf("CENTER", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public bool IsRight
        {
            get
            {
                return !string.IsNullOrEmpty(Estilo) &&
                       Estilo.IndexOf("RIGHT", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public bool IsBarcode
        {
            get
            {
                return !string.IsNullOrEmpty(EsCodigoBarras) &&
                       (EsCodigoBarras == "1" || EsCodigoBarras.Equals("true", StringComparison.OrdinalIgnoreCase));
            }
        }

        public FontSize GetFontSize()
        {
            switch (Tamanio)
            {
                case 1: return FontSize.Small;
                case 2: return FontSize.Normal;
                case 3: return FontSize.Large;
                case 4: return FontSize.DoubleWidth;
                case 5: return FontSize.DoubleHeight;
                default: return FontSize.Normal;
            }
        }

        public Alignment GetAlignment()
        {
            if (IsCenter) return Alignment.Center;
            if (IsRight) return Alignment.Right;
            return Alignment.Left;
        }
    }

    public static class LineaParser
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(LineaParser));

        public static List<LineaData> ParseFromJson(string lineasJson)
        {
            var result = new List<LineaData>();
            if (string.IsNullOrEmpty(lineasJson)) return result;

            try
            {
                var array = JArray.Parse(lineasJson);
                foreach (var item in array)
                {
                    var obj = item as JObject;
                    if (obj == null) continue;

                    result.Add(ParseLinea(obj));
                }
            }
            catch (Exception ex)
            {
                Log.Error("[LINEA] Error parseando lineasimprimir JSON: " + ex.Message);
            }

            return result;
        }

        public static LineaData ParseLinea(JObject json)
        {
            var linea = new LineaData();

            JToken token;
            if (json.TryGetValue("texto", out token) && token.Type != JTokenType.Null)
                linea.Texto = token.ToString();

            if (json.TryGetValue("estilo", out token) && token.Type != JTokenType.Null)
                linea.Estilo = token.ToString();

            if (json.TryGetValue("fuente", out token) && token.Type != JTokenType.Null)
                linea.Fuente = token.ToString();

            if (json.TryGetValue("lines_total", out token) && token.Type != JTokenType.Null)
            {
                int val;
                if (int.TryParse(token.ToString(), out val))
                    linea.LinesTotal = val;
            }

            if (json.TryGetValue("tamaniosaltolinea", out token) && token.Type != JTokenType.Null)
            {
                int val;
                if (int.TryParse(token.ToString(), out val))
                    linea.TamanioSaltoLinea = val;
            }

            if (json.TryGetValue("tamanio", out token) && token.Type != JTokenType.Null)
            {
                int val;
                if (int.TryParse(token.ToString(), out val))
                    linea.Tamanio = val;
            }

            if (json.TryGetValue("escodigobarras", out token) && token.Type != JTokenType.Null)
                linea.EsCodigoBarras = token.ToString();

            return linea;
        }

        public static byte[] BuildFromLineas(IPrinterDriver driver, List<LineaData> lineas)
        {
            var builder = new EscPosCommandBuilder(driver);
            builder.Init();

            foreach (var linea in lineas)
            {
                // Set alignment
                builder.SetAlignment(linea.GetAlignment());

                // Set font size
                if (linea.Tamanio > 0)
                {
                    builder.SetFontSize(linea.GetFontSize());
                }

                // Set bold
                if (linea.IsBold)
                {
                    builder.SetBold(true);
                }

                // Set line spacing if specified
                if (linea.TamanioSaltoLinea > 0)
                {
                    builder.SetLineSpacing(linea.TamanioSaltoLinea);
                }

                // Print barcode or text
                if (linea.IsBarcode && !string.IsNullOrEmpty(linea.Texto))
                {
                    builder.PrintBarcode(linea.Texto, 73, 80, 3); // CODE128, height=80, width=3
                }
                else if (!string.IsNullOrEmpty(linea.Texto))
                {
                    builder.Text(linea.Texto);
                }

                // Add line breaks
                if (linea.LinesTotal > 0)
                {
                    builder.Feed(linea.LinesTotal);
                }
                else
                {
                    builder.NewLine();
                }

                // Reset formatting for next line
                builder.ResetFormatting();
            }

            builder.Cut(CutType.Partial);
            return builder.Build();
        }
    }
}
