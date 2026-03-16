using System;
using System.Linq;
using log4net;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// SERVICIO: Verificación de salud de red.
    /// PROPÓSITO: Comparar config actual con snapshot bueno y determinar estado.
    /// PRINCIPIO SRP: Solo responsable de verificación, no de captura ni almacenamiento.
    /// </summary>
    public class NetworkHealthChecker : INetworkHealthChecker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkHealthChecker));
        private readonly PrinterServiceDb _db;

        public NetworkHealthChecker(PrinterServiceDb db)
        {
            // RAZÓN: Inyección de dependencia (DIP)
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Verifica salud de red comparando config actual con última conocida buena.
        /// RAZÓN: Determina si PrinterServices cambió de red.
        /// </summary>
        public string CheckNetworkHealth(NetworkConfig currentConfig, NetworkConfig lastKnownGood)
        {
            // RAZÓN: Si no hay config actual, no se puede determinar
            if (currentConfig == null)
            {
                Log.Warn("[NET-HEALTH] Config actual null, retornando unknown");
                return "unknown";
            }

            // RAZÓN: Si no hay snapshot bueno, es primera ejecución
            if (lastKnownGood == null)
            {
                Log.Info("[NET-HEALTH] No hay red conocida buena, primera ejecución");
                return "unknown";
            }

            // RAZÓN: Comparar gateway MAC (identificador físico de red)
            bool sameNetwork = currentConfig.IsSameNetworkAs(lastKnownGood);

            if (sameNetwork)
            {
                Log.Debug("[NET-HEALTH] Red saludable (mismo gateway MAC)");
                return "healthy";
            }
            else
            {
                Log.Warn($"[NET-HEALTH] ⚠ RED CAMBIADA detectada:");
                Log.Warn($"  Gateway esperado: {lastKnownGood.GatewayMac} ({lastKnownGood.NetworkId})");
                Log.Warn($"  Gateway actual: {currentConfig.GatewayMac} ({currentConfig.NetworkId})");
                if (!string.IsNullOrEmpty(lastKnownGood.WifiSsid))
                {
                    Log.Warn($"  SSID esperado: {lastKnownGood.WifiSsid}");
                }
                if (!string.IsNullOrEmpty(currentConfig.WifiSsid))
                {
                    Log.Warn($"  SSID actual: {currentConfig.WifiSsid}");
                }
                
                return "changed";
            }
        }

        /// <summary>
        /// Actualiza estado actual de red en BD (singleton network_current).
        /// RAZÓN: PrintWorker consulta esto antes de cada impresión.
        /// </summary>
        public void UpdateCurrentNetworkStatus(NetworkConfig config, string status)
        {
            try
            {
                // RAZÓN: network_current es singleton (id=1, solo 1 fila)
                var current = _db.Table<NetworkCurrentEntity>()
                    .FirstOrDefault(c => c.Id == 1);

                if (current == null)
                {
                    // RAZÓN: Primera vez, crear registro
                    current = new NetworkCurrentEntity { Id = 1 };
                }

                // RAZÓN: Actualizar todos los campos con config actual
                if (config != null)
                {
                    current.GatewayMac = config.GatewayMac;
                    current.GatewayIp = config.GatewayIp;
                    current.PrinterServiceIp = config.PrinterServiceIp;
                    current.SubnetMask = config.SubnetMask;
                    current.NetworkId = config.NetworkId;
                    current.WifiSsid = config.WifiSsid;
                    current.AdapterName = config.AdapterName;
                    current.AdapterMac = config.AdapterMac;
                }

                current.Status = status;
                current.LastCheck = DateTime.Now;

                // RAZÓN: InsertOrReplace maneja ambos casos (nueva o update)
                _db.InsertOrReplace(current);

                Log.DebugFormat("[NET-HEALTH] Estado actualizado: {0}", status);
            }
            catch (Exception ex)
            {
                Log.Error("[NET-HEALTH] Error actualizando estado: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Obtiene estado actual de red desde BD.
        /// RAZÓN: PrintWorker consulta esto antes de imprimir.
        /// </summary>
        public string GetCurrentNetworkStatus()
        {
            try
            {
                // RAZÓN: Consultar singleton network_current
                var current = _db.Table<NetworkCurrentEntity>()
                    .FirstOrDefault(c => c.Id == 1);

                if (current == null)
                {
                    // RAZÓN: Aún no se ha hecho ningún check
                    return "unknown";
                }

                return current.Status ?? "unknown";
            }
            catch (Exception ex)
            {
                Log.Error("[NET-HEALTH] Error obteniendo estado: " + ex.Message, ex);
                return "unknown";
            }
        }
    }
}
