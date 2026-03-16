using System;
using log4net;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Monitoring;
using PrinterServices.Notifications;

namespace PrinterServices.Core.Network
{
    /// <summary>
    /// Helper para auto-resolver IP de impresoras cuando fallan las conexiones.
    /// FLUJO: Marca offline → Busca por MAC → Si encuentra nueva IP → Reintenta → Marca online.
    /// </summary>
    public static class PrinterIpResolver
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterIpResolver));

        /// <summary>
        /// Intenta resolver nueva IP de una impresora que no responde.
        /// 
        /// FLUJO:
        /// 1. La impresora no responde en su IP actual → ya marcada como offline
        /// 2. Buscar MAC en tabla ARP (búsqueda inversa MAC → IP)
        /// 3. Si encuentra MAC con IP diferente:
        ///    - Actualizar printer.Ip en BD
        ///    - Marcar ip_resuelta_por_arp = 1
        ///    - Verificar conectividad con nueva IP
        ///    - Si conecta OK → marcar online, retornar true
        /// 4. Si NO encuentra → mantener offline, retornar false
        /// 
        /// </summary>
        /// <param name="db">Instancia de base de datos.</param>
        /// <param name="printer">Entidad de impresora que no responde.</param>
        /// <param name="timeoutMs">Timeout para verificación de conectividad (default 3000ms).</param>
        /// <param name="cancellationToken">Token para cancelar la búsqueda si la impresora vuelve online.</param>
        /// <returns>
        /// True si se encontró nueva IP y la impresora responde.
        /// False si no se pudo resolver o la nueva IP tampoco responde.
        /// </returns>
        public static bool TryResolveNewIp(PrinterServiceDb db, PrinterEntity printer, int timeoutMs = 3000, System.Threading.CancellationToken cancellationToken = default)
        {
            // Validar que la impresora tenga MAC registrada
            if (string.IsNullOrEmpty(printer.MacAddress))
            {
                Log.WarnFormat("[IP-RESOLVER] Impresora {0} ({1}) NO tiene MAC registrada — no se puede auto-resolver",
                    printer.Nombre ?? printer.ImpresoraId, printer.Ip);
                return false; // Sin MAC no podemos buscar
            }

            try
            {
                Log.InfoFormat("[IP-RESOLVER] Buscando nueva IP para {0} (MAC: {1}, IP anterior: {2})",
                    printer.Nombre ?? printer.ImpresoraId, printer.MacAddress, printer.Ip);

                // 1️⃣ Buscar IP actual de esta MAC en tabla ARP (caché actual, ~10ms)
                string newIp = ArpHelper.FindIpByMac(printer.MacAddress);

                if (newIp == null)
                {
                    // MAC NO encontrada en caché → hacer SCAN ACTIVO de subred
                    Log.InfoFormat("[IP-RESOLVER] MAC {0} NO en caché ARP — iniciando scan activo de subred",
                        printer.MacAddress);
                    
                    // 2️⃣ Extraer subred de IP anterior (ej: "192.168.1.100" → "192.168.1")
                    string subnet = ArpHelper.GetSubnetFromIp(printer.Ip);
                    
                    if (!string.IsNullOrEmpty(subnet))
                    {
                        // 3️⃣ Scan activo de subred (paralelo, ~500ms para 254 hosts)
                        //    Envía ARP request a 192.168.1.1-254
                        //    Pobla caché ARP de Windows con todas las MACs que respondan
                        //    IMPORTANTE: Pasa cancellationToken para abortar si impresora vuelve online
                        ArpHelper.ScanSubnet(subnet, startHost: 1, endHost: 254, timeoutMs: 50, cancellationToken: cancellationToken);
                        
                        // 4️⃣ Buscar NUEVAMENTE en caché (ahora poblada con el scan)
                        newIp = ArpHelper.FindIpByMac(printer.MacAddress);
                        
                        if (newIp != null)
                        {
                            Log.InfoFormat("[IP-RESOLVER] ✅ MAC {0} encontrada tras scan activo: IP {1}",
                                printer.MacAddress, newIp);
                        }
                    }
                    
                    // Si aún es null, impresora realmente apagada o en otra red
                    if (newIp == null)
                    {
                        Log.WarnFormat("[IP-RESOLVER] MAC {0} NO encontrada tras scan — impresora apagada o fuera de red",
                            printer.MacAddress);
                        return false; // No se pudo resolver
                    }
                }

                if (newIp == printer.Ip)
                {
                    // Misma IP (el problema no es cambio de IP, es que la impresora está realmente offline)
                    Log.DebugFormat("[IP-RESOLVER] MAC {0} encontrada con la MISMA IP {1} — impresora realmente offline",
                        printer.MacAddress, newIp);
                    return false; // No hay nueva IP que probar
                }

                // ¡Encontramos la impresora con una IP DIFERENTE! (cambio por DHCP)
                Log.WarnFormat("[IP-RESOLVER] ⚠ Impresora {0} cambió IP: {1} → {2} (MAC: {3})",
                    printer.Nombre ?? printer.ImpresoraId, printer.Ip, newIp, printer.MacAddress);

                // Verificar si la nueva IP realmente responde (antes de actualizar BD)
                var status = PrinterStatusChecker.CheckSync(newIp, printer.Puerto, timeoutMs);

                if (!status.Online)
                {
                    // La nueva IP tampoco responde (posible conflicto de ARP cache)
                    Log.WarnFormat("[IP-RESOLVER] Nueva IP {0} NO responde — posible entrada ARP obsoleta",
                        newIp);
                    return false; // No actualizar BD si la nueva IP no funciona
                }

                // ✅ La nueva IP FUNCIONA — actualizar en base de datos
                string oldIp = printer.Ip; // Guardar para log y notificación
                printer.Ip = newIp; // Actualizar IP
                printer.IpResueltaPorArp = 1; // Marcar que fue auto-resuelta
                printer.EstadoOnline = 1; // Marcar ONLINE (la nueva IP responde)
                printer.DisponibleParaImprimir = status.DisponibleParaImprimir ? 1 : 0; // Actualizar disponibilidad
                printer.TienePapel = status.TienePapel ? 1 : 0; // Actualizar papel
                printer.TapaAbierta = status.TapaAbierta ? 1 : 0; // Actualizar tapa
                printer.UltimoCheck = DateTime.Now.ToString("o"); // Actualizar timestamp

                db.Update(printer); // Persistir cambios

                Log.InfoFormat("[IP-RESOLVER] ✅ Impresora {0} AUTO-RESUELTA: {1} → {2} (disponible={3})",
                    printer.Nombre ?? printer.ImpresoraId, oldIp, newIp, status.DisponibleParaImprimir);

                // ═══════════════════════════════════════════════════════
                // NOTIFICAR A QUIPUNETX SOBRE EL CAMBIO DE IP
                // ═══════════════════════════════════════════════════════
                try
                {
                    // Crear notificador y enviar cambio de IP a QuipuNetX
                    var notifier = new QuipuNetXNotifier(db, ConfigManager.Instance);
                    
                    // Ejecutar notificación de forma asíncrona (fire-and-forget)
                    // Si QuipuNetX está offline, se persistirá en BD para reintentos
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        await notifier.NotifyIpChangeAsync(printer.MacAddress, oldIp, newIp);
                    });
                    
                    Log.InfoFormat("[IP-RESOLVER] Notificación de cambio de IP enviada a QuipuNetX: MAC={0}", 
                        printer.MacAddress);
                }
                catch (Exception notifEx)
                {
                    // Error al notificar - loguear pero NO fallar el proceso de resolución
                    Log.Error("[IP-RESOLVER] Error al notificar cambio de IP a QuipuNetX", notifEx);
                    // Continuar - el cambio de IP ya está guardado en BD
                }

                return true; // Éxito: nueva IP encontrada y funcional
            }
            catch (Exception ex)
            {
                Log.Error("[IP-RESOLVER] Error intentando resolver IP para " + printer.ImpresoraId, ex);
                return false; // Error inesperado
            }
        }

        /// <summary>
        /// Variante async de TryResolveNewIp para uso en contextos async.
        /// Delega a la versión sync (ArpHelper es naturalmente sync, usa P/Invoke).
        /// </summary>
        public static System.Threading.Tasks.Task<bool> TryResolveNewIpAsync(
            PrinterServiceDb db, PrinterEntity printer, int timeoutMs = 3000)
        {
            // ArpHelper usa P/Invoke (sync por naturaleza), ejecutar en ThreadPool
            return System.Threading.Tasks.Task.Run(() => TryResolveNewIp(db, printer, timeoutMs));
        }
    }
}
