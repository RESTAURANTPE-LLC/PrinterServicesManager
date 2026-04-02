using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PrinterServices.Queue.Documents
{
    /// <summary>
    /// Factory que crea ITipoDocumento desde el JSON del request.
    ///
    /// PRINCIPIO OCP: Para agregar VentaDocument, solo crear la clase + un case aquí.
    /// </summary>
    public static class TipoDocumentoFactory
    {
        public static ITipoDocumento Create(JObject json, string tipoImpresion)
        {
            if (json == null || string.IsNullOrEmpty(tipoImpresion))
                return null;

            switch (tipoImpresion.ToLowerInvariant())
            {
                case "comanda":
                case "anulacion":
                    return CreateComandaDocument(json);
                default:
                    return null;
            }
        }

        private static ComandaDocument CreateComandaDocument(JObject json)
        {
            string cadenaHtml = GetString(json, "cadenaHTML");

            return new ComandaDocument
            {
                Operacion = GetString(json, "mesa_trazabilidadid"),
                Serie = GetString(json, "serie"),
                TipoImpresion = GetString(json, "tipoimpresion"),
                Area = GetString(json, "area"),
                Hora = GetString(json, "hora"),
                Mozo = GetString(json, "mozo"),
                MozoPedido = GetString(json, "mozopedido"),
                Salon = GetString(json, "salon"),
                Mesa = GetString(json, "mesa"),
                CantPax = GetString(json, "cantpax"),
                Cliente = GetString(json, "cliente"),
                Empresa = GetString(json, "empresa"),
                Comprobante = GetString(json, "comprobante"),
                Modalidad = GetString(json, "modalidad"),
                ModalidadPedido = GetString(json, "modalidad_pedido"),
                DeliveryId = GetString(json, "delivery_identificadorunico"),
                ModalidadEntrega = GetString(json, "modalidad_entrega_delivery"),
                HoraRecojo = GetString(json, "hora_recojo"),
                HoraEntrega = GetString(json, "hora_entrega"),
                Canal = GetString(json, "canal"),
                TiempoPreparacion = GetString(json, "tiempo_preparacion"),
                Subcuenta = GetString(json, "subcuenta"),
                Localizador = GetString(json, "localizador"),
                NotaMesa = GetString(json, "nota_mesa"),
                PedidoId = GetString(json, "pedido_id"),
                MotivoAnulacion = GetString(json, "motivo_anulacion"),
                TipoComanda = GetString(json, "tipo_comanda"),
                // Campos de anulación: AnuladoPor usa "mozo" (quién anuló),
                // DeliveryAnulacion usa "delivery_identificadorunico"
                AnuladoPor = GetString(json, "mozo"),
                DeliveryAnulacion = GetString(json, "delivery_identificadorunico"),
                ProductosHtml = ExtractProductosHtml(cadenaHtml)
            };
        }

        /// <summary>
        /// Extrae la parte de productos de la cadenaHTML (desde INICIO PEDIDO en adelante).
        /// La cabecera se ignora porque ComandaDocument la reconstruye desde sus propiedades.
        /// </summary>
        private static string ExtractProductosHtml(string cadenaHtml)
        {
            if (string.IsNullOrEmpty(cadenaHtml))
                return "";

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
                    // Omitir cabecera
                    if (text.StartsWith("Operaci", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("N\u00B0", System.StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("Orden")) continue;
                    if (text.StartsWith("MESA:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("AREA:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("HORA:", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.StartsWith("FECHA:", System.StringComparison.OrdinalIgnoreCase)) continue;
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
                    if (text.Contains("Pax)") && text.Contains("N\u00B0")) continue;
                    if (text.Contains("DUPLICADA") || text.Contains("RE-IMPRESION")
                        || text.Contains("REIMPRESION")) continue;
                    if (text.Contains("MODIFICACION") || text.Contains("COMANDA UPDATE")) continue;
                    if (Regex.IsMatch(text, @"^\*{3,}$")) continue;
                    if (text.StartsWith("(") && !Regex.IsMatch(text, @"^\(\d")) continue;
                    if (text.StartsWith("ANULADO")) continue;
                    if (text.StartsWith("COMANDADO")) continue;
                    if (text.StartsWith("MOTIVO ANULACION")) continue;
                    if (text.StartsWith("POR RECOGER") || text.StartsWith("RECOJO")) continue;

                    if (text.Contains("INICIO PEDIDO") || text.Contains("----------")
                        || (text.StartsWith("(") && Regex.IsMatch(text, @"^\(\d"))
                        || text.StartsWith("Cant"))
                    {
                        productosIniciados = true;
                    }
                    else
                    {
                        continue;
                    }
                }

                // Strip "INICIO PEDIDO" y "FIN PEDIDO" — ahora son campos del template
                if (Regex.IsMatch(text, @"INICIO\s+PEDIDO", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(text, @"FIN\s+PEDIDO", RegexOptions.IgnoreCase)) continue;

                // Strip líneas que son SOLO guiones/rayas (separadores puros antes del primer producto)
                // Estos ya se manejan como campos Separator/InicioPedido/FinPedido en el template
                if (sb.Length == 0 && Regex.IsMatch(text, @"^[-─━═]{3,}$")) continue;

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
