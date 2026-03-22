using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PrinterServices.Queue.Documents
{
    /// <summary>
    /// Factory que crea la implementación correcta de ITipoDocumento
    /// según el tipo de impresión del job.
    ///
    /// PRINCIPIO OCP: Para agregar un nuevo tipo (VentaDocument, EgresoDocument),
    /// solo se crea la clase y se agrega un case aquí. No se modifica PrintJob ni PrintWorker.
    /// </summary>
    public static class TipoDocumentoFactory
    {
        /// <summary>
        /// Crea un ITipoDocumento desde el JSON del request.
        /// Extrae los campos específicos del tipo de documento y separa
        /// la cadenaHTML en cabecera (campos individuales) + cuerpo (productos HTML).
        /// </summary>
        /// <param name="json">JSON completo del request de impresión</param>
        /// <param name="tipoImpresion">Tipo: "comanda", "venta", etc.</param>
        /// <returns>ITipoDocumento o null si el tipo no aplica</returns>
        public static ITipoDocumento Create(JObject json, string tipoImpresion)
        {
            if (json == null || string.IsNullOrEmpty(tipoImpresion))
                return null;

            switch (tipoImpresion.ToLowerInvariant())
            {
                case "comanda":
                case "anulacion":
                    return CreateComandaDocument(json);

                // Futuros tipos:
                // case "venta":
                //     return CreateVentaDocument(json);
                // case "precuenta":
                //     return CreatePrecuentaDocument(json);

                default:
                    return null;
            }
        }

        private static ComandaDocument CreateComandaDocument(JObject json)
        {
            string cadenaHtml = GetString(json, "cadenaHTML");

            var doc = new ComandaDocument
            {
                Operacion = GetString(json, "mesa_trazabilidadid"),
                Serie = GetString(json, "serie"),
                TipoImpresion = GetString(json, "tipoimpresion"),
                Area = GetString(json, "area"),
                Hora = GetString(json, "hora"),
                Mozo = GetString(json, "mozo"),
                Salon = GetString(json, "salon"),
                Mesa = GetString(json, "mesa"),
                CantPax = GetString(json, "cantpax"),
                Cliente = GetString(json, "cliente"),
                Modalidad = GetString(json, "modalidad"),
                ProductosHtml = ExtractProductosHtml(cadenaHtml)
            };

            return doc;
        }

        /// <summary>
        /// Extrae la parte de productos del HTML, omitiendo los campos de cabecera.
        /// La cabecera se reconstruye desde los campos individuales en ComandaDocument.
        /// Los productos empiezan a partir de "INICIO PEDIDO" o la primera línea con "(N)".
        /// </summary>
        private static string ExtractProductosHtml(string cadenaHtml)
        {
            if (string.IsNullOrEmpty(cadenaHtml))
                return "";

            // Separar por <br>
            string[] segments = Regex.Split(cadenaHtml, @"<br\s*/?>", RegexOptions.IgnoreCase);

            bool productosIniciados = false;
            var sb = new System.Text.StringBuilder();

            foreach (string segment in segments)
            {
                string seg = segment.Trim();
                if (string.IsNullOrEmpty(seg)) continue;

                string text = Regex.Replace(seg, @"<[^>]+>", "").Trim();
                if (string.IsNullOrEmpty(text)) continue;

                if (!productosIniciados)
                {
                    // Omitir líneas de cabecera (se reconstruyen desde campos individuales)
                    if (text.StartsWith("Operaci", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("N°", System.StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("Orden")) continue;
                    if (text.StartsWith("MESA:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("AREA:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("HORA:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("MOZO:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("SALA:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("CLIENTE:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("PEDIDO:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("CANAL:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("SUBCUENTA:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("LOCALIZADOR:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("TIEMPO PREPARACION:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("NOTA GENERAL:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("DELIVERY", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("VENTA R", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.Contains("Pax)") && text.Contains("N°")) continue;
                    if (text.Contains("DUPLICADA") || text.Contains("RE-IMPRESION")
                        || text.Contains("REIMPRESION")) continue;
                    if (text.Contains("MODIFICACION") || text.Contains("COMANDA UPDATE")) continue;
                    if (Regex.IsMatch(text, @"^\*{3,}$")) continue;
                    // Cliente entre paréntesis en cabecera
                    if (text.StartsWith("(") && !Regex.IsMatch(text, @"^\(\d")) continue;

                    // Si llegamos a separador o producto, inicia la zona de productos
                    if (text.Contains("INICIO PEDIDO") || text.Contains("----------")
                        || (text.StartsWith("(") && Regex.IsMatch(text, @"^\(\d")))
                    {
                        productosIniciados = true;
                    }
                    else
                    {
                        continue;
                    }
                }

                sb.Append(seg).Append("<br>");
            }

            return sb.ToString();
        }

        private static string GetString(JObject json, string key)
        {
            JToken token;
            if (json.TryGetValue(key, System.StringComparison.OrdinalIgnoreCase, out token)
                && token.Type != JTokenType.Null)
            {
                string val = token.ToString();
                return string.IsNullOrEmpty(val) ? null : val;
            }
            return null;
        }
    }
}
