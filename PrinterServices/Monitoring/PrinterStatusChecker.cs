using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace PrinterServices.Monitoring
{
    public class PrinterStatus
    {
        public bool Online { get; set; }                      // ¿Responde en la red? (conexión TCP exitosa)
        public bool DisponibleParaImprimir { get; set; }      // ¿Puede imprimir ahora? (Online && !TapaAbierta && TienePapel)
        public bool TienePapel { get; set; }
        public bool TapaAbierta { get; set; }
        public bool ErrorRecuperable { get; set; }
        public string RawStatus { get; set; }
        public string ErrorMessage { get; set; }

        public static PrinterStatus Offline(string error)
        {
            return new PrinterStatus
            {
                Online = false,
                TienePapel = true,
                TapaAbierta = false,
                ErrorMessage = error
            };
        }
    }

    public static class PrinterStatusChecker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterStatusChecker));

        // DLE EOT commands (real-time status request)
        // DLE EOT 1 — Printer status
        private static readonly byte[] DLE_EOT_PRINTER = { 0x10, 0x04, 0x01 };
        // DLE EOT 2 — Offline status
        private static readonly byte[] DLE_EOT_OFFLINE = { 0x10, 0x04, 0x02 };
        // DLE EOT 3 — Error status
        private static readonly byte[] DLE_EOT_ERROR = { 0x10, 0x04, 0x03 };
        // DLE EOT 4 — Paper sensor status
        private static readonly byte[] DLE_EOT_PAPER = { 0x10, 0x04, 0x04 };

        public static async Task<PrinterStatus> CheckAsync(string ip, int port, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ip))
            {
                return PrinterStatus.Offline("IP vacía");
            }

            if (port <= 0) port = 9100;
            if (timeoutMs <= 0) timeoutMs = 3000;

            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;

                // Connect with timeout
                var connectResult = socket.BeginConnect(ip, port, null, null);
                bool connected = connectResult.AsyncWaitHandle.WaitOne(timeoutMs, true);

                if (!connected || !socket.Connected)
                {
                    return PrinterStatus.Offline("No se pudo conectar a " + ip + ":" + port);
                }

                // Si llegamos aquí → conexión TCP exitosa → impresora está en la red
                var status = new PrinterStatus { Online = true, TienePapel = true };

                // Send DLE EOT 1 — Printer status
                byte printerByte = await SendAndReceiveByte(socket, DLE_EOT_PRINTER, timeoutMs);
                // Bit 3: 0=ready, 1=not ready (pero YA está conectada vía TCP)
                bool printerReady = (printerByte & 0x08) == 0;

                // Send DLE EOT 2 — Offline causes
                byte offlineByte = await SendAndReceiveByte(socket, DLE_EOT_OFFLINE, timeoutMs);
                // Bit 2: cover open
                status.TapaAbierta = (offlineByte & 0x04) != 0;

                // Send DLE EOT 3 — Error status
                byte errorByte = await SendAndReceiveByte(socket, DLE_EOT_ERROR, timeoutMs);
                // Bit 6: auto-recoverable error
                status.ErrorRecuperable = (errorByte & 0x40) != 0;

                // Send DLE EOT 4 — Paper sensor
                byte paperByte = await SendAndReceiveByte(socket, DLE_EOT_PAPER, timeoutMs);
                // Bits 5,6: 00=paper present, other=no paper
                status.TienePapel = (paperByte & 0x60) == 0;

                // Calcular disponibilidad: todas las condiciones deben ser OK
                status.DisponibleParaImprimir = printerReady && !status.TapaAbierta && status.TienePapel;

                status.RawStatus = string.Format("P:{0:X2} O:{1:X2} E:{2:X2} S:{3:X2}",
                    printerByte, offlineByte, errorByte, paperByte);

                Log.DebugFormat("[STATUS] {0}:{1} → online={2} disponible={3} papel={4} tapa={5} raw={6}",
                    ip, port, status.Online, status.DisponibleParaImprimir, 
                    status.TienePapel, status.TapaAbierta, status.RawStatus);

                return status;
            }
            catch (SocketException ex)
            {
                Log.DebugFormat("[STATUS] {0}:{1} → SocketException: {2}", ip, port, ex.Message);
                return PrinterStatus.Offline(ex.Message);
            }
            catch (TimeoutException)
            {
                Log.DebugFormat("[STATUS] {0}:{1} → Timeout", ip, port);
                return PrinterStatus.Offline("Timeout");
            }
            catch (Exception ex)
            {
                Log.DebugFormat("[STATUS] {0}:{1} → Error: {2}", ip, port, ex.Message);
                return PrinterStatus.Offline(ex.Message);
            }
            finally
            {
                if (socket != null)
                {
                    try
                    {
                        if (socket.Connected)
                        {
                            socket.Shutdown(SocketShutdown.Both);
                        }
                        socket.Close();
                    }
                    catch { }
                }
            }
        }

        public static PrinterStatus CheckSync(string ip, int port, int timeoutMs)
        {
            if (string.IsNullOrEmpty(ip))
            {
                return PrinterStatus.Offline("IP vacía");
            }

            if (port <= 0) port = 9100;
            if (timeoutMs <= 0) timeoutMs = 3000;

            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;

                var connectResult = socket.BeginConnect(ip, port, null, null);
                bool connected = connectResult.AsyncWaitHandle.WaitOne(timeoutMs, true);

                if (!connected || !socket.Connected)
                {
                    return PrinterStatus.Offline("No se pudo conectar a " + ip + ":" + port);
                }

                // Si llegamos aquí → conexión TCP exitosa → impresora está en la red
                var status = new PrinterStatus { Online = true, TienePapel = true };

                // DLE EOT 1 — Printer status
                byte printerByte = SendAndReceiveByteSync(socket, DLE_EOT_PRINTER, timeoutMs);
                // Bit 3: 0=ready, 1=not ready
                bool printerReady = (printerByte & 0x08) == 0;

                // DLE EOT 2 — Offline causes
                byte offlineByte = SendAndReceiveByteSync(socket, DLE_EOT_OFFLINE, timeoutMs);
                status.TapaAbierta = (offlineByte & 0x04) != 0;

                // DLE EOT 3 — Error status
                byte errorByte = SendAndReceiveByteSync(socket, DLE_EOT_ERROR, timeoutMs);
                status.ErrorRecuperable = (errorByte & 0x40) != 0;

                // DLE EOT 4 — Paper sensor
                byte paperByte = SendAndReceiveByteSync(socket, DLE_EOT_PAPER, timeoutMs);
                status.TienePapel = (paperByte & 0x60) == 0;

                // Calcular disponibilidad: todas las condiciones deben ser OK
                status.DisponibleParaImprimir = printerReady && !status.TapaAbierta && status.TienePapel;

                status.RawStatus = string.Format("P:{0:X2} O:{1:X2} E:{2:X2} S:{3:X2}",
                    printerByte, offlineByte, errorByte, paperByte);

                return status;
            }
            catch (Exception ex)
            {
                return PrinterStatus.Offline(ex.Message);
            }
            finally
            {
                if (socket != null)
                {
                    try
                    {
                        if (socket.Connected) socket.Shutdown(SocketShutdown.Both);
                        socket.Close();
                    }
                    catch { }
                }
            }
        }

        private static Task<byte> SendAndReceiveByte(Socket socket, byte[] command, int timeoutMs)
        {
            // Reutilizar la implementación síncrona ya que el método original era bloqueante de todos modos
            // Esto elimina el warning CS1998 y mantiene el mismo comportamiento
            return Task.FromResult(SendAndReceiveByteSync(socket, command, timeoutMs));
        }

        private static byte SendAndReceiveByteSync(Socket socket, byte[] command, int timeoutMs)
        {
            socket.Send(command, 0, command.Length, SocketFlags.None);

            if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectRead))
            {
                return 0x00;
            }

            var buffer = new byte[4];
            int received = socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
            return received > 0 ? buffer[0] : (byte)0x00;
        }
    }
}
