using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Microsoft.Win32.SafeHandles;

namespace PrinterServices.Transport
{
    /// <summary>
    /// Transporte USB que identifica la impresora por VID+PID+Serial (inmutable)
    /// y resuelve el DevicePath actual cada vez que se conecta.
    /// Si el usuario cambió la impresora de puerto USB, la encuentra automáticamente.
    /// Análogo a TcpTransport pero para dispositivos USB locales.
    /// </summary>
    public class UsbTransport : ITransport
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(UsbTransport));

        private readonly string _usbUniqueKey;   // VID+PID+Serial (identidad inmutable)
        private string _lastDevicePath;           // Último device path conocido (cache para detectar cambios)
        private FileStream _stream;
        private SafeFileHandle _handle;
        private bool _disposed;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        private const uint GENERIC_WRITE = 0x40000000;
        private const uint GENERIC_READ = 0x80000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;

        public bool IsConnected
        {
            get { return _stream != null && _handle != null && !_handle.IsInvalid; }
        }

        /// <summary>
        /// Último device path resuelto. Útil para loguear y detectar cambios de puerto.
        /// </summary>
        public string LastResolvedDevicePath
        {
            get { return _lastDevicePath; }
        }

        /// <param name="usbUniqueKey">Identidad inmutable: "04B8_0202_J9SG012345"</param>
        public UsbTransport(string usbUniqueKey)
        {
            _usbUniqueKey = usbUniqueKey;
        }

        public Task ConnectAsync(CancellationToken ct)
        {
            Disconnect();

            // ═══════════════════════════════════════════════════════════════════
            // PASO 1: Resolver UniqueKey → DevicePath actual
            // Análogo a resolver MAC → IP en impresoras de red (ArpScanWorker)
            // ═══════════════════════════════════════════════════════════════════
            var device = UsbDeviceEnumerator.FindByUniqueKey(_usbUniqueKey);

            if (device == null)
            {
                throw new IOException(string.Format(
                    "Impresora USB '{0}' no encontrada. ¿Está conectada?", _usbUniqueKey));
            }

            string devicePath = device.DevicePath;

            // Detectar si cambió de puerto USB (device path diferente al último conocido)
            if (_lastDevicePath != null && _lastDevicePath != devicePath)
            {
                Log.InfoFormat("[USB] Impresora {0} cambió de puerto USB: {1} → {2}",
                    _usbUniqueKey, _lastDevicePath, devicePath);
            }
            _lastDevicePath = devicePath;

            // ═══════════════════════════════════════════════════════════════════
            // PASO 2: Abrir dispositivo USB vía CreateFile (raw access)
            // ═══════════════════════════════════════════════════════════════════
            Log.DebugFormat("[USB] Abriendo {0} (path: {1})", _usbUniqueKey, devicePath);

            _handle = CreateFile(
                devicePath,
                GENERIC_WRITE | GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (_handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(string.Format(
                    "No se pudo abrir impresora USB '{0}' (Win32 error: {1}). "
                    + "¿Otra aplicación tiene el dispositivo abierto?",
                    _usbUniqueKey, error));
            }

            _stream = new FileStream(_handle, FileAccess.ReadWrite);
            Log.DebugFormat("[USB] Conectado a {0}", _usbUniqueKey);

            return Task.FromResult(0);
        }

        public async Task SendAsync(byte[] data, CancellationToken ct)
        {
            if (_stream == null)
                throw new InvalidOperationException("Dispositivo USB no abierto");

            await _stream.WriteAsync(data, 0, data.Length, ct);
            await _stream.FlushAsync(ct);
        }

        public async Task<byte[]> ReceiveAsync(int length, int timeoutMs, CancellationToken ct)
        {
            if (_stream == null)
                throw new InvalidOperationException("Dispositivo USB no abierto");

            var buffer = new byte[length];
            int totalRead = 0;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeoutMs);
                try
                {
                    while (totalRead < length)
                    {
                        int bytesRead = await _stream.ReadAsync(
                            buffer, totalRead, length - totalRead, cts.Token);
                        if (bytesRead == 0) break;
                        totalRead += bytesRead;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout de lectura — devolver lo que se leyó
                    Log.DebugFormat("[USB] Timeout de lectura después de {0}ms, bytes leídos: {1}",
                        timeoutMs, totalRead);
                }
            }

            if (totalRead < length)
            {
                var result = new byte[totalRead];
                Array.Copy(buffer, result, totalRead);
                return result;
            }
            return buffer;
        }

        public void Disconnect()
        {
            try
            {
                if (_stream != null)
                {
                    _stream.Close();
                    _stream = null;
                }
            }
            catch { }

            try
            {
                if (_handle != null && !_handle.IsInvalid)
                {
                    _handle.Close();
                    _handle = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Transporte USB que recibe un DevicePath ya resuelto.
    /// Usado internamente por UsbPrinterStatusChecker para evitar doble enumeración
    /// (FindByUniqueKey ya encontró el DevicePath, no re-enumerar).
    /// Para uso externo (PrintWorker), usar UsbTransport que resuelve automáticamente.
    /// </summary>
    internal class UsbTransportDirect : ITransport
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(UsbTransportDirect));

        private readonly string _devicePath;
        private FileStream _stream;
        private SafeFileHandle _handle;
        private bool _disposed;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        private const uint GENERIC_WRITE = 0x40000000;
        private const uint GENERIC_READ = 0x80000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;

        public bool IsConnected
        {
            get { return _stream != null && _handle != null && !_handle.IsInvalid; }
        }

        /// <param name="devicePath">Device path ya resuelto (ej: \\?\usb#vid_04b8&pid_0202#...)</param>
        public UsbTransportDirect(string devicePath)
        {
            _devicePath = devicePath;
        }

        public Task ConnectAsync(CancellationToken ct)
        {
            Disconnect();

            _handle = CreateFile(
                _devicePath,
                GENERIC_WRITE | GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (_handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(string.Format(
                    "No se pudo abrir dispositivo USB (Win32 error: {0})", error));
            }

            _stream = new FileStream(_handle, FileAccess.ReadWrite);
            return Task.FromResult(0);
        }

        public async Task SendAsync(byte[] data, CancellationToken ct)
        {
            if (_stream == null)
                throw new InvalidOperationException("Dispositivo USB no abierto");

            await _stream.WriteAsync(data, 0, data.Length, ct);
            await _stream.FlushAsync(ct);
        }

        public async Task<byte[]> ReceiveAsync(int length, int timeoutMs, CancellationToken ct)
        {
            if (_stream == null)
                throw new InvalidOperationException("Dispositivo USB no abierto");

            var buffer = new byte[length];
            int totalRead = 0;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeoutMs);
                try
                {
                    while (totalRead < length)
                    {
                        int bytesRead = await _stream.ReadAsync(
                            buffer, totalRead, length - totalRead, cts.Token);
                        if (bytesRead == 0) break;
                        totalRead += bytesRead;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }

            if (totalRead < length)
            {
                var result = new byte[totalRead];
                Array.Copy(buffer, result, totalRead);
                return result;
            }
            return buffer;
        }

        public void Disconnect()
        {
            try { _stream?.Close(); _stream = null; } catch { }
            try
            {
                if (_handle != null && !_handle.IsInvalid)
                {
                    _handle.Close();
                    _handle = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed) { Disconnect(); _disposed = true; }
        }
    }
}
