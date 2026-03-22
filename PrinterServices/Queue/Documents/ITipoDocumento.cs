using System.Collections.Generic;
using PrinterServices.Rendering;

namespace PrinterServices.Queue.Documents
{
    /// <summary>
    /// Interfaz para documentos de impresión tipados.
    /// Cada tipo (Comanda, Venta, etc.) implementa esta interfaz
    /// y sabe cómo generar sus líneas de renderizado.
    ///
    /// PRINCIPIO ISP: Interfaz mínima — solo lo que PrintWorker necesita.
    /// PRINCIPIO OCP: Nuevos tipos se agregan sin modificar PrintJob ni PrintWorker.
    /// </summary>
    public interface ITipoDocumento
    {
        /// <summary>
        /// Genera la lista de líneas listas para renderizar como bitmap.
        /// Cada línea tiene texto + fuente + tamaño + estilo + alineación.
        /// No depende de HTML — preserva toda la información del template.
        /// </summary>
        List<RenderLine> GenerarLineas();
    }
}
