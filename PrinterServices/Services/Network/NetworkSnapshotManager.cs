using System;
using System.Linq;
using log4net;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// SERVICIO: Gestión de snapshots de redes conocidas buenas.
    /// PROPÓSITO: CRUD de network_snapshots en BD.
    /// PRINCIPIO SRP: Solo responsable de persistir/recuperar snapshots, no de capturarlos ni analizarlos.
    /// </summary>
    public class NetworkSnapshotManager : INetworkSnapshotManager
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkSnapshotManager));
        private readonly PrinterServiceDb _db;

        public NetworkSnapshotManager(PrinterServiceDb db)
        {
            // RAZÓN: Inyección de dependencia (DIP)
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Registra o actualiza snapshot después de impresión exitosa.
        /// RAZÓN: INSERT si no existe, UPDATE print_count si ya existe.
        /// </summary>
        public void RecordSuccessfulPrint(NetworkConfig config)
        {
            if (config == null)
            {
                Log.Warn("[SNAPSHOT-MGR] Config null, no se puede registrar");
                return;
            }

            try
            {
                // RAZÓN: Buscar snapshot existente para esta red (gateway_mac + network_id)
                var existing = _db.Table<NetworkSnapshotEntity>()
                    .FirstOrDefault(s => s.GatewayMac == config.GatewayMac 
                                      && s.NetworkId == config.NetworkId);

                if (existing != null)
                {
                    // RAZÓN: Ya existe, solo actualizar contador y timestamp
                    existing.PrintCount++;
                    existing.LastSuccessfulPrint = DateTime.Now;
                    existing.PrinterServiceIp = config.PrinterServiceIp;
                    existing.GatewayIp = config.GatewayIp;
                    _db.Update(existing);

                    Log.DebugFormat("[SNAPSHOT-MGR] Snapshot actualizado: {0} ({1} impresiones)",
                        config.NetworkId, existing.PrintCount);
                }
                else
                {
                    // RAZÓN: No existe, crear nuevo snapshot
                    var entity = new NetworkSnapshotEntity
                    {
                        GatewayMac = config.GatewayMac,
                        AdapterMac = config.AdapterMac,
                        GatewayIp = config.GatewayIp,
                        PrinterServiceIp = config.PrinterServiceIp,
                        SubnetMask = config.SubnetMask,
                        NetworkId = config.NetworkId,
                        WifiSsid = config.WifiSsid,
                        AdapterName = config.AdapterName,
                        DnsPrimary = config.DnsPrimary,
                        DnsSecondary = config.DnsSecondary,
                        LastSuccessfulPrint = DateTime.Now,
                        PrintCount = 1,
                        IsTrusted = 1
                    };

                    _db.Insert(entity);

                    Log.InfoFormat("[SNAPSHOT-MGR] Nuevo snapshot creado: {0} (gateway: {1})",
                        config.NetworkId, config.GatewayMac);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[SNAPSHOT-MGR] Error registrando snapshot: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Obtiene última red conocida buena (más reciente con impresión exitosa).
        /// RAZÓN: Para comparar con config actual en NetworkWatcher.
        /// </summary>
        public NetworkConfig GetLastKnownGoodNetwork()
        {
            try
            {
                // RAZÓN: Obtener snapshot más reciente con is_trusted=1
                var entity = _db.Table<NetworkSnapshotEntity>()
                    .Where(s => s.IsTrusted == 1)
                    .OrderByDescending(s => s.LastSuccessfulPrint)
                    .FirstOrDefault();

                if (entity == null)
                {
                    Log.Debug("[SNAPSHOT-MGR] No hay snapshots conocidos (primera ejecución)");
                    return null;
                }

                // RAZÓN: Convertir entity a NetworkConfig
                var config = NetworkConfig.Create(
                    gatewayMac: entity.GatewayMac,
                    gatewayIp: entity.GatewayIp,
                    printerServiceIp: entity.PrinterServiceIp,
                    subnetMask: entity.SubnetMask,
                    networkId: entity.NetworkId,
                    wifiSsid: entity.WifiSsid,
                    adapterName: entity.AdapterName,
                    adapterMac: entity.AdapterMac,
                    dnsPrimary: entity.DnsPrimary,
                    dnsSecondary: entity.DnsSecondary
                );

                Log.DebugFormat("[SNAPSHOT-MGR] Última red conocida: {0} ({1} impresiones)",
                    entity.NetworkId, entity.PrintCount);

                return config;
            }
            catch (Exception ex)
            {
                Log.Error("[SNAPSHOT-MGR] Error obteniendo última red conocida: " + ex.Message, ex);
                return null;
            }
        }

        /// <summary>
        /// Verifica si existe snapshot para una config específica.
        /// RAZÓN: Para detectar si es primera vez en esta red.
        /// </summary>
        public bool HasSnapshotFor(NetworkConfig config)
        {
            if (config == null) return false;

            try
            {
                // RAZÓN: Buscar por gateway_mac y network_id
                var exists = _db.Table<NetworkSnapshotEntity>()
                    .Any(s => s.GatewayMac == config.GatewayMac 
                           && s.NetworkId == config.NetworkId);

                return exists;
            }
            catch (Exception ex)
            {
                Log.Error("[SNAPSHOT-MGR] Error verificando snapshot: " + ex.Message, ex);
                return false;
            }
        }
    }
}
