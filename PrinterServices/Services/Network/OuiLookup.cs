using System.Collections.Generic;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// SERVICIO: Lookup de fabricante por OUI (primeros 3 bytes de la MAC address).
    /// PROPOSITO: Identificar fabricante de dispositivos descubiertos en la red.
    /// Diccionario estatico con ~100 OUI comunes en entornos de restaurantes/POS.
    /// </summary>
    public static class OuiLookup
    {
        private static readonly Dictionary<string, string> OuiDatabase = new Dictionary<string, string>
        {
            // Impresoras termicas (POS)
            { "00:26:AB", "Seiko Epson" },
            { "00:1B:B1", "Seiko Epson" },
            { "64:EB:8C", "Seiko Epson" },
            { "00:11:62", "Star Micronics" },
            { "00:01:E3", "Bixolon" },
            { "00:1A:62", "Bixolon" },
            { "74:F0:7D", "Bixolon" },
            { "00:13:7B", "Mototola/Zebra" },
            { "00:07:4D", "Zebra" },
            { "00:23:68", "Zebra" },
            { "D4:20:6D", "HID Global (iZettle/POS)" },

            // Routers y networking
            { "00:1E:58", "D-Link" },
            { "1C:7E:E5", "D-Link" },
            { "00:1D:7E", "Cisco-Linksys" },
            { "00:26:99", "Cisco" },
            { "00:17:95", "Cisco" },
            { "00:1A:A1", "Cisco" },
            { "00:50:56", "VMware" },
            { "00:0C:29", "VMware" },
            { "04:18:D6", "Ubiquiti" },
            { "24:A4:3C", "Ubiquiti" },
            { "78:8A:20", "Ubiquiti" },
            { "B4:FB:E4", "Ubiquiti" },
            { "18:E8:29", "Ubiquiti" },
            { "F0:9F:C2", "Ubiquiti" },
            { "74:AC:B9", "Ubiquiti" },
            { "C0:4A:00", "TP-Link" },
            { "50:C7:BF", "TP-Link" },
            { "EC:08:6B", "TP-Link" },
            { "14:CC:20", "TP-Link" },
            { "60:32:B1", "TP-Link" },
            { "E8:48:B8", "TP-Link" },
            { "B0:95:75", "TP-Link" },
            { "A4:2B:B0", "TP-Link" },
            { "30:B5:C2", "TP-Link" },
            { "F4:F2:6D", "TP-Link" },
            { "E4:8D:8C", "Routerboard/MikroTik" },
            { "D4:CA:6D", "Routerboard/MikroTik" },
            { "00:0C:42", "Routerboard/MikroTik" },
            { "6C:3B:6B", "Routerboard/MikroTik" },
            { "CC:2D:E0", "Routerboard/MikroTik" },
            { "74:4D:28", "Routerboard/MikroTik" },
            { "2C:C8:1B", "Routerboard/MikroTik" },
            { "08:55:31", "Routerboard/MikroTik" },
            { "48:8F:5A", "Routerboard/MikroTik" },
            { "B8:69:F4", "Routerboard/MikroTik" },
            { "18:FD:74", "Routerboard/MikroTik" },
            { "00:24:D7", "Tenda" },
            { "C8:3A:35", "Tenda" },
            { "00:26:5A", "D-Link" },

            // Dispositivos comunes
            { "B8:27:EB", "Raspberry Pi" },
            { "DC:A6:32", "Raspberry Pi" },
            { "E4:5F:01", "Raspberry Pi" },
            { "00:25:90", "Super Micro" },
            { "AC:1F:6B", "Super Micro" },
            { "00:1C:C0", "Intel" },
            { "00:1E:67", "Intel" },
            { "3C:97:0E", "Wistron (PC generico)" },
            { "F0:1F:AF", "Dell" },
            { "00:14:22", "Dell" },
            { "18:03:73", "Dell" },
            { "00:1A:4A", "Qnap" },
            { "00:08:9B", "ICP Electronics (POS terminal)" },

            // Dispositivos moviles / tablets (comunes en restaurantes)
            { "AC:BC:32", "Apple" },
            { "F0:D4:F6", "Apple" },
            { "00:CD:FE", "Apple" },
            { "3C:22:FB", "Apple" },
            { "F4:F9:51", "Apple" },
            { "A4:83:E7", "Apple" },
            { "00:17:C9", "Samsung" },
            { "00:21:19", "Samsung" },
            { "94:35:0A", "Samsung" },
            { "C0:BD:D1", "Samsung" },
            { "8C:F5:A3", "Samsung" },
            { "00:1E:58", "D-Link" },
            { "28:6C:07", "Xiaomi" },
            { "64:CC:2E", "Xiaomi" },
            { "78:11:DC", "Xiaomi" },
            { "9C:99:A0", "Xiaomi" },
            { "3C:BD:D8", "LG Electronics" },
            { "00:AA:70", "LG Electronics" },
            { "00:22:F4", "Huawei" },
            { "00:E0:FC", "Huawei" },
            { "48:46:FB", "Huawei" },
            { "70:72:0D", "Huawei" },
            { "88:53:95", "Huawei" },

            // POS / Payment terminals
            { "00:04:F2", "Polycom" },
            { "58:6D:8F", "Cisco-Meraki" },
            { "00:18:0A", "Cisco-Meraki" },
            { "AC:17:02", "Cisco-Meraki" },
        };

        /// <summary>
        /// Busca el fabricante por los primeros 3 bytes de la MAC.
        /// Retorna null si no se encuentra.
        /// </summary>
        public static string GetVendor(string macAddress)
        {
            if (string.IsNullOrEmpty(macAddress) || macAddress.Length < 8)
                return null;

            // Normalizar a formato XX:XX:XX (primeros 3 bytes)
            string normalized = macAddress.ToUpperInvariant()
                .Replace("-", ":")
                .Substring(0, 8); // "AA:BB:CC"

            string vendor;
            return OuiDatabase.TryGetValue(normalized, out vendor) ? vendor : null;
        }

        /// <summary>
        /// Infiere el tipo de dispositivo basandose en el vendor.
        /// </summary>
        public static string InferDeviceType(string vendor)
        {
            if (string.IsNullOrEmpty(vendor)) return "unknown";

            var v = vendor.ToLowerInvariant();
            if (v.Contains("epson") || v.Contains("star") || v.Contains("bixolon") || v.Contains("zebra"))
                return "printer";
            if (v.Contains("cisco") || v.Contains("tp-link") || v.Contains("d-link") || v.Contains("ubiquiti") ||
                v.Contains("mikrotik") || v.Contains("tenda") || v.Contains("meraki") || v.Contains("linksys"))
                return "router";
            if (v.Contains("apple") || v.Contains("samsung") || v.Contains("xiaomi") || v.Contains("huawei") || v.Contains("lg"))
                return "mobile";
            if (v.Contains("raspberry"))
                return "sbc";
            if (v.Contains("dell") || v.Contains("intel") || v.Contains("wistron") || v.Contains("super micro"))
                return "computer";

            return "unknown";
        }
    }
}
