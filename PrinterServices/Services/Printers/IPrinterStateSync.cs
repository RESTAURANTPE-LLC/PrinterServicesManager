using System.Collections.Generic;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Printers
{
    /// <summary>
    /// Interface para propagación de estado entre impresoras según principio DIP.
    /// RAZÓN: StatusMonitor depende de abstracción, permite testing y cambiar implementación.
    /// </summary>
    public interface IPrinterStateSync
    {
        /// <summary>
        /// Propaga estado de un dispositivo físico a todas sus impresoras lógicas asociadas.
        /// RAZÓN: Si BARRA y BARRA 2 comparten MAC, ambas deben reflejar el mismo estado online/offline.
        /// </summary>
        /// <param name="sourceDevice">Impresora representante cuyo estado se propagará</param>
        /// <param name="allPrinters">Lista completa de impresoras registradas</param>
        /// <returns>Cantidad de impresoras actualizadas (sin contar la source)</returns>
        /// <example>
        /// Input: BARRA (online=1, MAC=AA..), [BARRA, BARRA 2 (MAC=AA..), COCINA (MAC=BB..)]
        /// Output: 1 (BARRA 2 actualizada con estado de BARRA)
        /// </example>
        int SyncStateToRelatedPrinters(PrinterEntity sourceDevice, List<PrinterEntity> allPrinters);
    }
}
