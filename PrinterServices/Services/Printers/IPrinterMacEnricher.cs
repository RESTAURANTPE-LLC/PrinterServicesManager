using PrinterServices.Data.Models;

namespace PrinterServices.Services.Printers
{
    /// <summary>
    /// Interface para enriquecimiento de MACs según principio DIP (Dependency Inversion).
    /// RAZÓN: StatusMonitor depende de abstracción, no de implementación concreta.
    /// BENEFICIO: Permite testing con mocks, cambiar implementación sin tocar StatusMonitor.
    /// </summary>
    public interface IPrinterMacEnricher
    {
        /// <summary>
        /// Enriquece impresoras sin MAC obteniéndola vía ARP desde su IP.
        /// RAZÓN: QuipuNetX puede enviar impresoras sin MAC (solo IP), necesitamos el identificador físico.
        /// </summary>
        /// <param name="printer">Entidad de impresora a enriquecer</param>
        /// <returns>true si se obtuvo y asignó MAC exitosamente, false si no</returns>
        bool TryEnrichMac(PrinterEntity printer);

        /// <summary>
        /// Normaliza MAC existente a formato estándar sin separadores.
        /// RAZÓN: Garantizar formato consistente en BD para comparaciones.
        /// </summary>
        /// <param name="printer">Entidad de impresora con MAC a normalizar</param>
        /// <returns>true si se normalizó exitosamente, false si MAC inválida</returns>
        bool TryNormalizeMac(PrinterEntity printer);
    }
}
