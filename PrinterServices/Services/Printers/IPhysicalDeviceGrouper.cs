using System.Collections.Generic;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Printers
{
    /// <summary>
    /// Interface para agrupamiento de impresoras por dispositivo físico según principio DIP.
    /// RAZÓN: StatusMonitor depende de abstracción, permite testing y cambiar implementación.
    /// </summary>
    public interface IPhysicalDeviceGrouper
    {
        /// <summary>
        /// Agrupa impresoras por dispositivo físico usando MAC como identificador único.
        /// RAZÓN: QuipuNetX puede tener múltiples registros (BARRA, BARRA 2) para el mismo dispositivo físico.
        /// Solo debe monitorearse UNA VEZ cada dispositivo de red para evitar overhead redundante.
        /// </summary>
        /// <param name="printers">Lista completa de impresoras registradas</param>
        /// <returns>Diccionario con clave=MAC normalizada, valor=impresora representante del grupo</returns>
        /// <example>
        /// Input: [BARRA (MAC:AA..), BARRA 2 (MAC:AA..), COCINA (MAC:BB..)]
        /// Output: { "AA..": BARRA, "BB..": COCINA }
        /// </example>
        Dictionary<string, PrinterEntity> GroupByPhysicalDevice(List<PrinterEntity> printers);
    }
}
