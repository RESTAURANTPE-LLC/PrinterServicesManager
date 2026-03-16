using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using log4net;
using PrinterServices.Core.Network;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// SERVICIO: Captura de configuración de red actual del sistema.
    /// PROPÓSITO: Obtener gateway, IP propia, SSID WiFi, DNS, etc.
    /// PRINCIPIO SRP: Solo responsable de capturar config, no de almacenarla ni analizarla.
    /// </summary>
    public class NetworkConfigCapture : INetworkConfigCapture
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(NetworkConfigCapture));

        /// <summary>
        /// Captura configuración de red actual completa.
        /// RAZÓN: Obtiene todos los datos necesarios para NetworkConfig.
        /// </summary>
        public NetworkConfig CaptureCurrentConfig()
        {
            try
            {
                // RAZÓN: Obtener adaptador de red activo (ethernet o WiFi con gateway)
                var activeAdapter = GetActiveNetworkAdapter();
                if (activeAdapter == null)
                {
                    Log.Warn("[NET-CAPTURE] No se encontró adaptador de red activo");
                    return null;
                }

                // RAZÓN: Obtener gateway predeterminado del adaptador activo
                var gatewayIp = GetGatewayIp(activeAdapter);
                if (gatewayIp == null)
                {
                    Log.Warn("[NET-CAPTURE] No se encontró gateway predeterminado");
                    return null;
                }

                // RAZÓN: Obtener MAC del gateway vía ARP (identificador físico de red)
                var gatewayMacPhysical = ArpHelper.GetMacFromIp(gatewayIp.ToString());
                if (gatewayMacPhysical == null)
                {
                    Log.Warn($"[NET-CAPTURE] No se pudo obtener MAC del gateway {gatewayIp}");
                    return null;
                }
                var gatewayMac = BitConverter.ToString(gatewayMacPhysical.GetAddressBytes()).Replace("-", ":");

                // RAZÓN: Obtener IP propia del adaptador activo
                var myIp = GetMyIpAddress(activeAdapter);
                if (myIp == null)
                {
                    Log.Warn("[NET-CAPTURE] No se pudo obtener IP propia");
                    return null;
                }

                // RAZÓN: Obtener subnet mask del adaptador
                var subnetMask = GetSubnetMask(activeAdapter);

                // RAZÓN: Calcular network ID (IP & SubnetMask)
                var networkId = CalculateNetworkId(myIp, subnetMask);

                // RAZÓN: Obtener MAC del adaptador propio
                var physicalAddress = activeAdapter.GetPhysicalAddress();
                var adapterMac = MacAddressNormalizer.Normalize(
                    BitConverter.ToString(physicalAddress.GetAddressBytes()).Replace("-", ":"));

                // RAZÓN: Obtener SSID si es WiFi (NULL si ethernet)
                string wifiSsid = null;
                if (activeAdapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    wifiSsid = GetWifiSsid(); // Intenta obtener SSID, puede fallar
                }

                // RAZÓN: Obtener DNS servers
                var dnsAddresses = activeAdapter.GetIPProperties().DnsAddresses
                    .Where(dns => dns.AddressFamily == AddressFamily.InterNetwork)
                    .ToList();

                string dnsPrimary = dnsAddresses.Count > 0 ? dnsAddresses[0].ToString() : null;
                string dnsSecondary = dnsAddresses.Count > 1 ? dnsAddresses[1].ToString() : null;

                // RAZÓN: Crear NetworkConfig con factory method (validación interna)
                var config = NetworkConfig.Create(
                    gatewayMac: gatewayMac,
                    gatewayIp: gatewayIp.ToString(),
                    printerServiceIp: myIp.ToString(),
                    subnetMask: subnetMask.ToString(),
                    networkId: networkId,
                    wifiSsid: wifiSsid,
                    adapterName: activeAdapter.Name,
                    adapterMac: adapterMac,
                    dnsPrimary: dnsPrimary,
                    dnsSecondary: dnsSecondary
                );

                Log.DebugFormat("[NET-CAPTURE] Config capturada: {0}", config);
                return config;
            }
            catch (Exception ex)
            {
                Log.Error("[NET-CAPTURE] Error capturando config de red: " + ex.Message, ex);
                return null;
            }
        }

        /// <summary>
        /// Obtiene el adaptador de red activo (con gateway y operativo).
        /// RAZÓN: Prioriza ethernet sobre WiFi si ambos están activos.
        /// </summary>
        private NetworkInterface GetActiveNetworkAdapter()
        {
            // RAZÓN: Obtener todos los adaptadores operativos
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.OperationalStatus == OperationalStatus.Up)
                .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(a => a.GetIPProperties().GatewayAddresses.Any())
                .ToList();

            if (!adapters.Any()) return null;

            // RAZÓN: Priorizar ethernet sobre WiFi (más estable)
            var ethernet = adapters.FirstOrDefault(a =>
                a.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                a.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet);

            if (ethernet != null) return ethernet;

            // RAZÓN: Si no hay ethernet, usar WiFi
            var wifi = adapters.FirstOrDefault(a =>
                a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

            if (wifi != null) return wifi;

            // RAZÓN: Si no hay ethernet ni WiFi, retornar el primero disponible
            return adapters.First();
        }

        /// <summary>
        /// Obtiene IP del gateway del adaptador.
        /// RAZÓN: Gateway es el router, identificado por su IP.
        /// </summary>
        private IPAddress GetGatewayIp(NetworkInterface adapter)
        {
            // RAZÓN: Obtener primera dirección de gateway IPv4
            var gatewayAddress = adapter.GetIPProperties().GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

            return gatewayAddress?.Address;
        }

        /// <summary>
        /// Obtiene IP propia del adaptador.
        /// RAZÓN: Necesaria para calcular network ID y diagnóstico.
        /// </summary>
        private IPAddress GetMyIpAddress(NetworkInterface adapter)
        {
            // RAZÓN: Obtener primera dirección IPv4 unicast del adaptador
            var unicastAddress = adapter.GetIPProperties().UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);

            return unicastAddress?.Address;
        }

        /// <summary>
        /// Obtiene subnet mask del adaptador.
        /// RAZÓN: Necesaria para calcular network ID.
        /// </summary>
        private IPAddress GetSubnetMask(NetworkInterface adapter)
        {
            // RAZÓN: Obtener subnet mask de primera dirección IPv4
            var unicastAddress = adapter.GetIPProperties().UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);

            return unicastAddress?.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
        }

        /// <summary>
        /// Calcula network ID haciendo IP AND SubnetMask.
        /// RAZÓN: Identificador lógico de subred (ej: 192.168.1.0/24).
        /// </summary>
        private string CalculateNetworkId(IPAddress ip, IPAddress subnetMask)
        {
            // RAZÓN: Bitwise AND entre IP y subnet mask
            byte[] ipBytes = ip.GetAddressBytes();
            byte[] maskBytes = subnetMask.GetAddressBytes();
            byte[] networkBytes = new byte[4];

            for (int i = 0; i < 4; i++)
            {
                networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            }

            var networkIp = new IPAddress(networkBytes);

            // RAZÓN: Calcular CIDR (número de bits en 1 de la máscara)
            int cidr = 0;
            foreach (byte b in maskBytes)
            {
                cidr += CountBits(b);
            }

            return $"{networkIp}/{cidr}";
        }

        /// <summary>
        /// Cuenta bits en 1 de un byte.
        /// RAZÓN: Necesario para calcular CIDR de subnet mask.
        /// </summary>
        private int CountBits(byte b)
        {
            int count = 0;
            while (b != 0)
            {
                count += b & 1;
                b >>= 1;
            }
            return count;
        }

        /// <summary>
        /// Intenta obtener SSID de WiFi activa (solo Windows).
        /// RAZÓN: Diagnóstico para usuario ("Reconectar a WiFi X").
        /// </summary>
        private string GetWifiSsid()
        {
            try
            {
                // RAZÓN: Usar netsh en Windows para obtener SSID
                // Alternativa: usar Native WiFi API (más complejo)
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    // RAZÓN: Parsear línea "SSID : NombreWiFi"
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var ssidLine = lines.FirstOrDefault(l => l.Trim().StartsWith("SSID"));

                    if (ssidLine != null)
                    {
                        var parts = ssidLine.Split(':');
                        if (parts.Length >= 2)
                        {
                            return parts[1].Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[NET-CAPTURE] No se pudo obtener SSID WiFi: " + ex.Message);
            }

            return null;
        }
    }
}
