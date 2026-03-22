using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using log4net;

namespace PrinterServices.Transport
{
    /// <summary>
    /// Enumera y resuelve dispositivos USB usando Windows SetupAPI.
    /// Análogo a ArpHelper/PrinterIpResolver para impresoras de red.
    /// RESPONSABILIDAD: Encontrar el DevicePath actual de una impresora USB
    /// dado su VID+PID+Serial, sin importar en qué puerto USB esté conectada.
    /// </summary>
    public static class UsbDeviceEnumerator
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(UsbDeviceEnumerator));

        // ═══════════════════════════════════════════════════════════════════
        // SetupAPI P/Invoke — Windows API para enumerar dispositivos USB
        // ═══════════════════════════════════════════════════════════════════

        // GUID para clase "USB Printing Support" (usbprint.sys)
        // Windows asigna esta GUID a impresoras conectadas por USB
        private static readonly Guid GUID_DEVINTERFACE_USB_PRINT =
            new Guid("28d78fad-5a12-11d1-ae5b-0000f803a8c2");

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, uint memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            uint property, out uint propertyRegDataType,
            byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private const uint SPDRP_FRIENDLYNAME = 0x0C;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        // ═══════════════════════════════════════════════════════════════════
        // API pública
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Enumera TODAS las impresoras USB conectadas en este momento.
        /// Devuelve identidad completa (VID, PID, Serial, DevicePath actual).
        /// Análogo a un "ARP scan" pero para USB.
        /// </summary>
        public static List<UsbDeviceIdentity> EnumerateUsbPrinters()
        {
            var result = new List<UsbDeviceIdentity>();
            var guid = GUID_DEVINTERFACE_USB_PRINT;

            IntPtr deviceInfoSet = SetupDiGetClassDevs(
                ref guid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == new IntPtr(-1))
            {
                Log.Warn("[USB-ENUM] SetupDiGetClassDevs falló, error: " + Marshal.GetLastWin32Error());
                return result;
            }

            try
            {
                uint index = 0;
                var interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));

                while (SetupDiEnumDeviceInterfaces(
                    deviceInfoSet, IntPtr.Zero, ref guid, index++, ref interfaceData))
                {
                    string devicePath = GetDevicePath(deviceInfoSet, ref interfaceData);
                    if (string.IsNullOrEmpty(devicePath))
                        continue;

                    var identity = ParseDevicePath(devicePath);
                    if (identity == null)
                        continue;

                    // Obtener nombre amigable del dispositivo
                    identity.FriendlyName = GetFriendlyName(deviceInfoSet, ref interfaceData);

                    result.Add(identity);
                    Log.DebugFormat("[USB-ENUM] Encontrada: {0}", identity);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[USB-ENUM] Error enumerando dispositivos USB: " + ex.Message, ex);
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return result;
        }

        /// <summary>
        /// Busca una impresora USB específica por su UniqueKey (VID+PID+Serial).
        /// Devuelve el DevicePath ACTUAL (puede haber cambiado de puerto).
        /// Análogo a PrinterIpResolver.ResolveByMac() para impresoras de red.
        /// </summary>
        /// <returns>Identidad con DevicePath actual, o null si no está conectada</returns>
        public static UsbDeviceIdentity FindByUniqueKey(string uniqueKey)
        {
            if (string.IsNullOrEmpty(uniqueKey)) return null;

            var printers = EnumerateUsbPrinters();
            foreach (var printer in printers)
            {
                if (string.Equals(printer.UniqueKey, uniqueKey, StringComparison.OrdinalIgnoreCase))
                {
                    return printer;
                }
            }

            Log.DebugFormat("[USB-ENUM] Impresora {0} no encontrada (desconectada?)", uniqueKey);
            return null;
        }

        /// <summary>
        /// Verifica si un dispositivo USB específico está conectado AHORA.
        /// Rápido: solo enumera y compara, no abre el dispositivo.
        /// </summary>
        public static bool IsConnected(string uniqueKey)
        {
            return FindByUniqueKey(uniqueKey) != null;
        }

        // ═══════════════════════════════════════════════════════════════════
        // Internos
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Obtiene el device path de una interfaz USB.
        /// El device path es el identificador que se pasa a CreateFile para abrir el dispositivo.
        /// </summary>
        private static string GetDevicePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
        {
            // Primer llamado: obtener tamaño necesario
            uint requiredSize;
            SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet, ref interfaceData, IntPtr.Zero, 0,
                out requiredSize, IntPtr.Zero);

            if (requiredSize == 0)
                return null;

            // Segundo llamado: obtener datos
            // SP_DEVICE_INTERFACE_DETAIL_DATA tiene cbSize variable, hay que hacerlo manual
            IntPtr detailDataBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                // cbSize del SP_DEVICE_INTERFACE_DETAIL_DATA:
                // En x64 = 8 (4 bytes cbSize + 4 bytes padding antes del string)
                // En x86 = 6 (4 bytes cbSize + 2 bytes para char alignment)
                Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : 6);

                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                if (SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, detailDataBuffer,
                    requiredSize, out requiredSize, ref devInfoData))
                {
                    // El string empieza en offset 4 (después de cbSize)
                    return Marshal.PtrToStringAuto(new IntPtr(detailDataBuffer.ToInt64() + 4));
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(detailDataBuffer);
            }
        }

        /// <summary>
        /// Obtiene el nombre amigable del dispositivo (ej: "EPSON TM-T20II Receipt").
        /// </summary>
        private static string GetFriendlyName(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
        {
            // Necesitamos el SP_DEVINFO_DATA para consultar propiedades del dispositivo
            uint requiredSize;
            SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet, ref interfaceData, IntPtr.Zero, 0,
                out requiredSize, IntPtr.Zero);

            if (requiredSize == 0)
                return null;

            IntPtr detailDataBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : 6);

                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                if (!SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, detailDataBuffer,
                    requiredSize, out requiredSize, ref devInfoData))
                {
                    return null;
                }

                // Consultar SPDRP_FRIENDLYNAME
                byte[] buffer = new byte[512];
                uint propType;
                uint propSize;

                if (SetupDiGetDeviceRegistryProperty(
                    deviceInfoSet, ref devInfoData, SPDRP_FRIENDLYNAME,
                    out propType, buffer, (uint)buffer.Length, out propSize))
                {
                    // El string viene en Unicode, terminar antes del null
                    if (propSize > 2)
                    {
                        return Encoding.Unicode.GetString(buffer, 0, (int)propSize - 2);
                    }
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(detailDataBuffer);
            }
        }

        /// <summary>
        /// Parsea un device path de Windows para extraer VID, PID y Serial.
        /// Formato típico: \\?\usb#vid_04b8&pid_0202#J9SG012345#{28d78fad-...}
        /// El serial está entre el segundo y tercer '#'.
        /// </summary>
        private static UsbDeviceIdentity ParseDevicePath(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;

            // Regex para extraer VID, PID, Serial del device path
            // Formato: ...#vid_XXXX&pid_XXXX#SERIAL#{guid}
            var match = Regex.Match(devicePath,
                @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})#([^#]+)#",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                Log.DebugFormat("[USB-ENUM] No se pudo parsear device path: {0}", devicePath);
                return null;
            }

            return new UsbDeviceIdentity
            {
                Vid = match.Groups[1].Value.ToUpperInvariant(),
                Pid = match.Groups[2].Value.ToUpperInvariant(),
                SerialNumber = match.Groups[3].Value,
                DevicePath = devicePath
            };
        }
    }
}
