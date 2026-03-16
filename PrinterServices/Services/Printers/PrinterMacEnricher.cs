using System;
using log4net;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Printers
{
    /// <summary>
    /// Servicio para enriquecer impresoras con MAC según principio SRP (Single Responsibility).
    /// RESPONSABILIDAD ÚNICA: Obtener y normalizar MACs de impresoras.
    /// NO hace: Monitoreo de estado, agrupamiento, propagación de estado.
    /// </summary>
    public class PrinterMacEnricher : IPrinterMacEnricher
    {
        // Logger para diagnóstico de enriquecimiento de MACs
        // RAZÓN: Rastrear cuándo se obtienen MACs exitosamente o fallan
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterMacEnricher));

        // Referencia a BD para persistir MACs obtenidas
        // RAZÓN: Necesitamos guardar las MACs en BD para uso posterior
        private readonly PrinterServiceDb _db;

        /// <summary>
        /// Constructor con inyección de dependencias (DIP).
        /// RAZÓN: No crear instancias dentro de la clase, recibirlas desde fuera.
        /// </summary>
        public PrinterMacEnricher(PrinterServiceDb db)
        {
            // Validar dependencia inyectada no nula
            // RAZÓN: Fail-fast si no se configuró correctamente
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Enriquece impresora sin MAC obteniéndola vía ARP.
        /// RAZÓN: QuipuNetX puede registrar impresoras solo con IP, necesitamos el identificador físico (MAC).
        /// </summary>
        public bool TryEnrichMac(PrinterEntity printer)
        {
            // Validar entrada no nula
            // RAZÓN: Prevenir NullReferenceException
            if (printer == null)
            {
                return false;
            }

            // Si ya tiene MAC, no necesita enriquecimiento
            // RAZÓN: No hacer trabajo innecesario, optimización
            if (!string.IsNullOrEmpty(printer.MacAddress))
            {
                return false;
            }

            // Si no tiene IP, no podemos obtener MAC vía ARP
            // RAZÓN: ARP requiere IP para hacer la consulta
            if (string.IsNullOrEmpty(printer.Ip))
            {
                Log.WarnFormat("[MAC-ENRICH] Impresora {0} sin IP ni MAC - no se puede enriquecer",
                    printer.Nombre ?? printer.ImpresoraId);
                return false;
            }

            try
            {
                // Llamar a ArpHelper para obtener MAC desde IP
                // RAZÓN: ArpHelper encapsula lógica de P/Invoke a iphlpapi.dll
                var physicalAddr = ArpHelper.GetMacFromIp(printer.Ip);

                // Si no se obtuvo MAC (impresora offline o fuera de red)
                // RAZÓN: ARP solo funciona en la misma subred, puede fallar
                if (physicalAddr == null)
                {
                    Log.DebugFormat("[MAC-ENRICH] No se pudo obtener MAC para {0} ({1}) - impresora offline o fuera de subred",
                        printer.Nombre ?? printer.ImpresoraId, printer.Ip);
                    return false;
                }

                // Convertir PhysicalAddress a string
                // RAZÓN: ToString() devuelve formato con separadores, necesitamos normalizar
                string macString = physicalAddr.ToString();

                // Normalizar MAC a formato sin separadores
                // RAZÓN: Garantizar formato consistente en BD (AABBCCDDEEFF)
                string normalizedMac = MacAddressNormalizer.Normalize(macString);

                // Validar que normalización fue exitosa
                // RAZÓN: Prevenir almacenar MACs inválidas en BD
                if (string.IsNullOrEmpty(normalizedMac))
                {
                    Log.WarnFormat("[MAC-ENRICH] MAC obtenida es inválida para {0} ({1}): {2}",
                        printer.Nombre ?? printer.ImpresoraId, printer.Ip, macString);
                    return false;
                }

                // Asignar MAC normalizada a la entidad
                // RAZÓN: Actualizar objeto en memoria antes de persistir
                printer.MacAddress = normalizedMac;

                // Persistir en BD
                // RAZÓN: Guardar cambio para que StatusMonitor pueda usar MAC en próximos ciclos
                _db.Update(printer);

                // Loguear éxito para diagnóstico
                // RAZÓN: Rastrear cuándo se obtienen MACs exitosamente
                Log.InfoFormat("[MAC-ENRICH] ✓ MAC obtenida y guardada para {0} ({1}): {2}",
                    printer.Nombre ?? printer.ImpresoraId, printer.Ip,
                    MacAddressNormalizer.Format(normalizedMac)); // Formatear para legibilidad en logs

                return true; // Enriquecimiento exitoso
            }
            catch (Exception ex)
            {
                // Capturar cualquier error (timeout de red, BD bloqueada, etc.)
                // RAZÓN: No dejar que falle el ciclo completo de monitoreo por un error de enriquecimiento
                Log.WarnFormat("[MAC-ENRICH] Error obteniendo MAC para {0} ({1}): {2}",
                    printer.Nombre ?? printer.ImpresoraId, printer.Ip, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Normaliza MAC existente a formato estándar.
        /// RAZÓN: QuipuNetX puede enviar MACs con : o -, necesitamos formato único.
        /// </summary>
        public bool TryNormalizeMac(PrinterEntity printer)
        {
            // Validar entrada no nula
            // RAZÓN: Prevenir NullReferenceException
            if (printer == null)
            {
                return false;
            }

            // Si no tiene MAC, no hay nada que normalizar
            // RAZÓN: No hacer trabajo innecesario
            if (string.IsNullOrEmpty(printer.MacAddress))
            {
                return false;
            }

            try
            {
                // Normalizar MAC a formato sin separadores
                // RAZÓN: Garantizar formato consistente (AABBCCDDEEFF)
                string normalized = MacAddressNormalizer.Normalize(printer.MacAddress);

                // Si normalización falló (MAC inválida)
                // RAZÓN: No sobrescribir con null, mantener valor original
                if (string.IsNullOrEmpty(normalized))
                {
                    Log.WarnFormat("[MAC-ENRICH] MAC inválida en {0}: {1}",
                        printer.Nombre ?? printer.ImpresoraId, printer.MacAddress);
                    return false;
                }

                // Si ya está normalizada, no hacer nada
                // RAZÓN: Evitar UPDATE innecesario en BD
                if (normalized == printer.MacAddress)
                {
                    return false; // No cambió, pero no es error
                }

                // Actualizar MAC normalizada
                // RAZÓN: Convertir formato inconsistente a estándar
                string oldMac = printer.MacAddress;
                printer.MacAddress = normalized;

                // Persistir en BD
                // RAZÓN: Guardar cambio para próximos ciclos
                _db.Update(printer);

                // Loguear cambio para diagnóstico
                // RAZÓN: Rastrear cuándo se normalizan MACs
                Log.InfoFormat("[MAC-ENRICH] MAC normalizada para {0}: {1} → {2}",
                    printer.Nombre ?? printer.ImpresoraId,
                    MacAddressNormalizer.Format(oldMac),
                    MacAddressNormalizer.Format(normalized));

                return true; // Normalización exitosa
            }
            catch (Exception ex)
            {
                // Capturar cualquier error (BD bloqueada, etc.)
                // RAZÓN: No dejar que falle el ciclo completo de monitoreo
                Log.WarnFormat("[MAC-ENRICH] Error normalizando MAC para {0}: {1}",
                    printer.Nombre ?? printer.ImpresoraId, ex.Message);
                return false;
            }
        }
    }
}
