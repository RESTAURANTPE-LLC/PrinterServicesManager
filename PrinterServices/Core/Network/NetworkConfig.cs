using System;

namespace PrinterServices.Core.Network
{
    /// <summary>
    /// VALUE OBJECT: Configuración de red capturada en un momento específico.
    /// PROPÓSITO: Inmutable, representa snapshot de red sin lógica de negocio.
    /// PRINCIPIO: Value Object pattern - igualdad por valor, no por referencia.
    /// </summary>
    public class NetworkConfig
    {
        // RAZÓN: Properties read-only para inmutabilidad
        public string GatewayMac { get; private set; }
        public string GatewayIp { get; private set; }
        public string PrinterServiceIp { get; private set; }
        public string SubnetMask { get; private set; }
        public string NetworkId { get; private set; }
        public string WifiSsid { get; private set; }
        public string AdapterName { get; private set; }
        public string AdapterMac { get; private set; }
        public string DnsPrimary { get; private set; }
        public string DnsSecondary { get; private set; }
        public DateTime CapturedAt { get; private set; }

        /// <summary>
        /// Constructor privado para forzar uso de factory method.
        /// RAZÓN: Validación centralizada en Create().
        /// </summary>
        private NetworkConfig() { }

        /// <summary>
        /// Factory method para crear NetworkConfig validado.
        /// RAZÓN: Asegura que solo se crean objetos válidos.
        /// </summary>
        public static NetworkConfig Create(
            string gatewayMac,
            string gatewayIp,
            string printerServiceIp,
            string subnetMask,
            string networkId,
            string wifiSsid,
            string adapterName,
            string adapterMac,
            string dnsPrimary,
            string dnsSecondary)
        {
            // RAZÓN: Validar campos críticos
            if (string.IsNullOrEmpty(gatewayMac))
                throw new ArgumentException("GatewayMac no puede estar vacío", nameof(gatewayMac));
            if (string.IsNullOrEmpty(networkId))
                throw new ArgumentException("NetworkId no puede estar vacío", nameof(networkId));

            return new NetworkConfig
            {
                GatewayMac = gatewayMac,
                GatewayIp = gatewayIp,
                PrinterServiceIp = printerServiceIp,
                SubnetMask = subnetMask,
                NetworkId = networkId,
                WifiSsid = wifiSsid,
                AdapterName = adapterName,
                AdapterMac = adapterMac,
                DnsPrimary = dnsPrimary,
                DnsSecondary = dnsSecondary,
                CapturedAt = DateTime.Now
            };
        }

        /// <summary>
        /// Verifica si esta config coincide con otra por gateway MAC.
        /// RAZÓN: Gateway MAC es el identificador físico único de red.
        /// </summary>
        public bool IsSameNetworkAs(NetworkConfig other)
        {
            if (other == null) return false;
            // RAZÓN: Comparación case-insensitive de MACs
            return string.Equals(this.GatewayMac, other.GatewayMac, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString()
        {
            return $"Network[{NetworkId}, Gateway:{GatewayMac}, Adapter:{AdapterName}]";
        }
    }
}
