namespace PrinterServices.Queue.Documents
{
    /// <summary>
    /// Interfaz para documentos de impresión tipados.
    /// Cada tipo de documento (Comanda, Venta, etc.) implementa esta interfaz
    /// con sus propias propiedades específicas y sabe cómo generar su HTML.
    ///
    /// PRINCIPIO ISP: La interfaz es mínima — solo expone lo que PrintWorker necesita.
    /// PRINCIPIO OCP: Nuevos tipos de documento se agregan sin modificar PrintJob ni PrintWorker.
    /// </summary>
    public interface ITipoDocumento
    {
        /// <summary>
        /// Genera la cadena HTML lista para renderizar como bitmap.
        /// Cada implementación arma el HTML con la estructura y orden que necesita.
        /// </summary>
        string GenerarHtml();
    }
}
