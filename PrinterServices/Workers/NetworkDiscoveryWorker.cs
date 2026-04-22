using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Services.Network;

namespace PrinterServices.Workers
{
    /// <summary>
    /// WORKER: Descubre dispositivos en la red local cada 1 hora.
    /// PROPOSITO: Escanear ARP table, resolver DNS/OUI, guardar en devices_on_network.
    /// PATRON: Identico a NetworkWatcher — Thread LongRunning + CancellationToken.
    /// No bloquea ningún worker existente.
    /// </summary>
    public class NetworkDiscoveryWorker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkDiscoveryWorker));
        private readonly PrinterServiceDb _db;
        private readonly ConfigManager _config;
        private CancellationTokenSource _cts;
        private Task _workerTask;
        private readonly SemaphoreSlim _manualSignal = new SemaphoreSlim(0, 1);

        public NetworkDiscoveryWorker(PrinterServiceDb db, ConfigManager config)
        {
            _db = db;
            _config = config;
        }

        public void Start()
        {
            if (_workerTask != null)
            {
                Log.Warn("[DISCOVERY] Ya esta iniciado, ignorando");
                return;
            }

            _cts = new CancellationTokenSource();

            _workerTask = Task.Factory.StartNew(
                () => RunLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Log.Info("[DISCOVERY] Iniciado (descubrimiento de dispositivos en red)");
        }

        public void Stop()
        {
            if (_cts != null)
            {
                Log.Info("[DISCOVERY] Deteniendo...");
                _cts.Cancel();
            }

            if (_workerTask != null)
            {
                try { _workerTask.Wait(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) { Log.Warn("[DISCOVERY] Error esperando finalizacion: " + ex.Message); }
            }

            Log.Info("[DISCOVERY] Detenido");
        }

        /// <summary>
        /// Forzar un escaneo inmediato (llamado desde el dashboard).
        /// </summary>
        public void ScanNow()
        {
            try
            {
                if (_manualSignal.CurrentCount == 0)
                    _manualSignal.Release();
            }
            catch { }
        }

        private void RunLoop(CancellationToken ct)
        {
            // Esperar 60s al arrancar para no interferir con inicializacion
            try { Task.Delay(TimeSpan.FromSeconds(60), ct).Wait(ct); }
            catch { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    PerformScan(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error("[DISCOVERY] Error en escaneo: " + ex.Message);
                }

                // Esperar intervalo (default 60 min) o señal manual
                try
                {
                    int intervalMinutes = _config.GetInt("NetworkDiscoveryIntervalMinutes", 60);
                    _manualSignal.Wait(TimeSpan.FromMinutes(intervalMinutes), ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        private void PerformScan(CancellationToken ct)
        {
            // Obtener red actual
            var networkCurrent = _db.Table<NetworkCurrentEntity>().FirstOrDefault(n => n.Id == 1);
            if (networkCurrent == null)
            {
                Log.Warn("[DISCOVERY] No hay datos de red actual, omitiendo escaneo");
                return;
            }

            string myIp = networkCurrent.PrinterServiceIp;
            string subnetMask = networkCurrent.SubnetMask ?? "255.255.255.0";
            string gatewayMac = networkCurrent.GatewayMac;
            string networkId = networkCurrent.NetworkId;

            if (string.IsNullOrEmpty(myIp))
            {
                Log.Warn("[DISCOVERY] IP propia no disponible, omitiendo escaneo");
                return;
            }

            // Calcular rango de IPs a escanear
            var ipsToScan = CalculateIpRange(myIp, subnetMask);
            Log.InfoFormat("[DISCOVERY] Escaneando {0} IPs en red {1}", ipsToScan.Count, networkId ?? "?");

            string now = DateTime.Now.ToString("o");
            int discovered = 0;
            int updated = 0;

            // Marcar todos como offline antes del escaneo
            try
            {
                _db.Execute("UPDATE devices_on_network SET is_online = 0");
            }
            catch { }

            foreach (var ip in ipsToScan)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // Intentar ARP para obtener MAC
                    var macPhysical = ArpHelper.GetMacFromIp(ip);
                    if (macPhysical == null) continue; // No respondio

                    string mac = BitConverter.ToString(macPhysical.GetAddressBytes()).Replace("-", ":");

                    // DNS reverso (best effort)
                    string hostname = null;
                    try
                    {
                        var hostEntry = Dns.GetHostEntry(ip);
                        if (hostEntry != null && !string.IsNullOrEmpty(hostEntry.HostName) && hostEntry.HostName != ip)
                            hostname = hostEntry.HostName;
                    }
                    catch { }

                    // OUI lookup
                    string vendor = OuiLookup.GetVendor(mac);
                    string deviceType = OuiLookup.InferDeviceType(vendor);

                    // Upsert: buscar por MAC
                    var existing = _db.Table<DeviceOnNetworkEntity>()
                        .FirstOrDefault(d => d.MacAddress == mac);

                    if (existing != null)
                    {
                        // Actualizar
                        existing.IpAddress = ip;
                        existing.LastSeenAt = now;
                        existing.IsOnline = 1;
                        existing.NetworkId = networkId;
                        existing.GatewayMac = gatewayMac;
                        if (hostname != null) existing.Hostname = hostname;
                        if (vendor != null) existing.Vendor = vendor;
                        existing.DeviceType = deviceType;
                        _db.Update(existing);
                        updated++;
                    }
                    else
                    {
                        // Nuevo dispositivo
                        _db.Insert(new DeviceOnNetworkEntity
                        {
                            IpAddress = ip,
                            MacAddress = mac,
                            Hostname = hostname,
                            Vendor = vendor,
                            DeviceType = deviceType,
                            FirstSeenAt = now,
                            LastSeenAt = now,
                            IsOnline = 1,
                            NetworkId = networkId,
                            GatewayMac = gatewayMac
                        });
                        discovered++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("[DISCOVERY] Error escaneando " + ip + ": " + ex.Message);
                }
            }

            Log.InfoFormat("[DISCOVERY] Escaneo completado: {0} nuevos, {1} actualizados", discovered, updated);
        }

        /// <summary>
        /// Calcula el rango de IPs de la subred (max 254 hosts para /24).
        /// </summary>
        private List<string> CalculateIpRange(string myIp, string subnetMask)
        {
            var result = new List<string>();

            try
            {
                var ip = IPAddress.Parse(myIp);
                var mask = IPAddress.Parse(subnetMask);
                var ipBytes = ip.GetAddressBytes();
                var maskBytes = mask.GetAddressBytes();

                // Calcular network address
                var networkBytes = new byte[4];
                for (int i = 0; i < 4; i++)
                    networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);

                // Calcular broadcast
                var broadcastBytes = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcastBytes[i] = (byte)(networkBytes[i] | ~maskBytes[i]);

                // Generar IPs (excluyendo network y broadcast)
                // Limitar a 254 para subredes /24 o mayores
                uint networkAddr = (uint)(networkBytes[0] << 24 | networkBytes[1] << 16 | networkBytes[2] << 8 | networkBytes[3]);
                uint broadcastAddr = (uint)(broadcastBytes[0] << 24 | broadcastBytes[1] << 16 | broadcastBytes[2] << 8 | broadcastBytes[3]);

                uint count = broadcastAddr - networkAddr - 1;
                if (count > 254) count = 254; // Limitar para no sobrecargar

                for (uint i = 1; i <= count; i++)
                {
                    uint addr = networkAddr + i;
                    string ipStr = string.Format("{0}.{1}.{2}.{3}",
                        (addr >> 24) & 0xFF, (addr >> 16) & 0xFF, (addr >> 8) & 0xFF, addr & 0xFF);
                    result.Add(ipStr);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[DISCOVERY] Error calculando rango: " + ex.Message);
            }

            return result;
        }
    }
}
