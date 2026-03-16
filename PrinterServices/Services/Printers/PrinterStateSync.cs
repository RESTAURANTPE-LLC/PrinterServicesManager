using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Printers
{
    /// <summary>
    /// Servicio para sincronizar estado entre impresoras del mismo dispositivo físico según principio SRP.
    /// RESPONSABILIDAD ÚNICA: Propagar estado de red de un dispositivo físico a todas sus impresoras lógicas.
    /// NO hace: Enriquecimiento de MAC, agrupamiento, verificación de estado.
    /// </summary>
    public class PrinterStateSync : IPrinterStateSync
    {
        // Logger para diagnóstico de sincronización de estado
        // RAZÓN: Rastrear cuántas impresoras se sincronizan por cada dispositivo físico
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterStateSync));

        // Referencia a BD para persistir cambios de estado
        // RAZÓN: Necesitamos actualizar múltiples registros cuando un dispositivo cambia de estado
        private readonly PrinterServiceDb _db;

        /// <summary>
        /// Constructor con inyección de dependencias (DIP).
        /// RAZÓN: No crear instancias dentro de la clase, recibirlas desde fuera.
        /// </summary>
        public PrinterStateSync(PrinterServiceDb db)
        {
            // Validar dependencia inyectada no nula
            // RAZÓN: Fail-fast si no se configuró correctamente
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Propaga estado de un dispositivo físico a todas sus impresoras lógicas relacionadas.
        /// ESCENARIO: BARRA (online) y BARRA 2 (offline) tienen misma MAC → ambas deben estar online.
        /// RAZÓN: Solo se monitorea UNA impresora por dispositivo físico, pero el estado debe reflejarse en TODAS.
        /// </summary>
        /// <param name="sourceDevice">Impresora representante cuyo estado fue verificado</param>
        /// <param name="allPrinters">Lista completa de impresoras registradas en BD</param>
        /// <returns>Cantidad de impresoras actualizadas (excluyendo la source)</returns>
        public int SyncStateToRelatedPrinters(PrinterEntity sourceDevice, List<PrinterEntity> allPrinters)
        {
            // Validar entrada no nula
            // RAZÓN: Prevenir NullReferenceException en operaciones posteriores
            if (sourceDevice == null)
            {
                Log.Warn("[STATE-SYNC] sourceDevice es null, no se puede sincronizar");
                return 0; // Retornar 0 = ninguna impresora actualizada
            }

            if (allPrinters == null || allPrinters.Count == 0)
            {
                // Si lista está vacía, no hay nada que sincronizar
                // RAZÓN: No es error, simplemente no hay impresoras relacionadas
                return 0;
            }

            // Si impresora fuente no tiene MAC, no podemos identificar dispositivos relacionados
            // RAZÓN: MAC es el único identificador físico confiable para agrupar
            if (string.IsNullOrEmpty(sourceDevice.MacAddress))
            {
                Log.DebugFormat("[STATE-SYNC] Impresora {0} sin MAC - no se puede sincronizar con relacionadas",
                    sourceDevice.Nombre ?? sourceDevice.ImpresoraId);
                return 0;
            }

            // Normalizar MAC de dispositivo fuente para comparación consistente
            // RAZÓN: "AA:BB:CC" debe coincidir con "AA-BB-CC" y "AABBCC"
            string sourceMacNormalized = MacAddressNormalizer.Normalize(sourceDevice.MacAddress);

            // Si normalización falló (MAC inválida), no se puede sincronizar
            // RAZÓN: No podemos comparar con MACs inválidas
            if (string.IsNullOrEmpty(sourceMacNormalized))
            {
                Log.WarnFormat("[STATE-SYNC] MAC inválida en {0}: {1}",
                    sourceDevice.Nombre ?? sourceDevice.ImpresoraId, sourceDevice.MacAddress);
                return 0;
            }

            // Buscar todas las impresoras con la misma MAC (excluyendo la source)
            // RAZÓN: BARRA 2, BARRA 3 deben tener mismo estado que BARRA si comparten MAC
            var relatedPrinters = allPrinters
                .Where(p =>
                    // NO sincronizar consigo misma
                    // RAZÓN: La source ya tiene el estado correcto
                    p.ImpresoraId != sourceDevice.ImpresoraId &&
                    
                    // Debe tener MAC para poder comparar
                    // RAZÓN: Sin MAC no podemos saber si es el mismo dispositivo
                    !string.IsNullOrEmpty(p.MacAddress) &&
                    
                    // MAC normalizada debe coincidir con la source
                    // RAZÓN: Mismo dispositivo físico = misma MAC
                    MacAddressNormalizer.Normalize(p.MacAddress) == sourceMacNormalized)
                .ToList(); // ToList() para materializar query antes de foreach

            // Si no hay impresoras relacionadas, no hacer nada
            // RAZÓN: Dispositivo físico tiene solo un registro lógico (caso común)
            if (relatedPrinters.Count == 0)
            {
                return 0; // 0 impresoras actualizadas
            }

            // Contador de impresoras actualizadas exitosamente
            // RAZÓN: Para retornar cantidad y logging de estadísticas
            int updatedCount = 0;

            // Iterar sobre cada impresora relacionada y sincronizar su estado
            // RAZÓN: Cada registro debe reflejar el estado del dispositivo físico
            foreach (var relatedPrinter in relatedPrinters)
            {
                try
                {
                    // CAMPOS A SINCRONIZAR:
                    // =====================

                    // 1. Estado de conectividad TCP (online/offline)
                    // RAZÓN: Si dispositivo físico está online, TODAS sus entradas deben reflejarlo
                    relatedPrinter.EstadoOnline = sourceDevice.EstadoOnline;

                    // 2. Disponibilidad para imprimir (considera papel, tapa, etc.)
                    // RAZÓN: Si dispositivo no puede imprimir, NINGUNA entrada debe intentar usarlo
                    relatedPrinter.DisponibleParaImprimir = sourceDevice.DisponibleParaImprimir;

                    // 3. Estado de papel
                    // RAZÓN: Estado físico compartido por todas las entradas lógicas
                    relatedPrinter.TienePapel = sourceDevice.TienePapel;

                    // 4. Estado de tapa
                    // RAZÓN: Estado físico compartido por todas las entradas lógicas
                    relatedPrinter.TapaAbierta = sourceDevice.TapaAbierta;

                    // 5. Timestamp de última verificación
                    // RAZÓN: Todas deben mostrar misma hora de última verificación
                    relatedPrinter.UltimoCheck = sourceDevice.UltimoCheck;

                    // 6. Sincronizar IP también (puede haber cambiado por DHCP)
                    // RAZÓN: Si PrinterIpResolver detectó nueva IP, propagar a todas las entradas
                    // ESCENARIO: BARRA tiene IP antigua, BARRA 2 debe actualizarse con IP nueva
                    relatedPrinter.Ip = sourceDevice.Ip;

                    // Persistir cambios en BD
                    // RAZÓN: Los jobs en cola deben usar el estado más reciente
                    _db.Update(relatedPrinter);

                    // Incrementar contador de éxitos
                    // RAZÓN: Para retornar cantidad total actualizada
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    // Capturar cualquier error de actualización (BD bloqueada, etc.)
                    // RAZÓN: Si falla una actualización, continuar con las demás
                    Log.WarnFormat("[STATE-SYNC] Error sincronizando estado a {0}: {1}",
                        relatedPrinter.Nombre ?? relatedPrinter.ImpresoraId, ex.Message);
                    // NO hacer throw, continuar con siguiente impresora
                }
            }

            // Loguear resultado de sincronización para diagnóstico
            // RAZÓN: Rastrear cuántas impresoras lógicas se sincronizan por dispositivo físico
            if (updatedCount > 0)
            {
                Log.InfoFormat("[STATE-SYNC] Estado propagado desde {0} a {1} impresora(s) relacionada(s) (MAC={2}, Online={3}, Disponible={4})",
                    sourceDevice.Nombre ?? sourceDevice.ImpresoraId,
                    updatedCount,
                    MacAddressNormalizer.Format(sourceMacNormalized), // Formatear para legibilidad
                    sourceDevice.EstadoOnline == 1 ? "Sí" : "No",
                    sourceDevice.DisponibleParaImprimir == 1 ? "Sí" : "No");
            }

            // Retornar cantidad de impresoras actualizadas
            // RAZÓN: Caller puede usar esto para estadísticas o decisiones
            return updatedCount;
        }
    }
}
