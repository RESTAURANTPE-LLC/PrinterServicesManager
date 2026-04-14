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
        public string ResolvedUsbDevicePath { get; set; }     // Solo USB: DevicePath resuelto (para detectar cambio de puerto)
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

                // ======[ DLE VALIDATION ]====== Validar respuesta DLE EOT.
                // TM-T20IIIL a veces no responde al primer intento (quirk del modelo).
                // Si todos los bytes son 0x00, reintentar UNA vez antes de asumir que DLE no responde.
                bool todosEnCero = printerByte == 0x00 && offlineByte == 0x00 && errorByte == 0x00 && paperByte == 0x00;

                if (todosEnCero)
                {
                    // Retry: esperar 150ms y reenviar los 4 comandos DLE EOT
                    Log.DebugFormat("[STATUS] {0}:{1} → DLE todo cero, reintentando...", ip, port);
                    Thread.Sleep(150);
                    printerByte = SendAndReceiveByteSync(socket, DLE_EOT_PRINTER, timeoutMs);
                    offlineByte = SendAndReceiveByteSync(socket, DLE_EOT_OFFLINE, timeoutMs);
                    errorByte = SendAndReceiveByteSync(socket, DLE_EOT_ERROR, timeoutMs);
                    paperByte = SendAndReceiveByteSync(socket, DLE_EOT_PAPER, timeoutMs);

                    todosEnCero = printerByte == 0x00 && offlineByte == 0x00 && errorByte == 0x00 && paperByte == 0x00;

                    if (!todosEnCero)
                    {
                        // Retry exitoso — recalcular estado con los nuevos bytes
                        printerReady = (printerByte & 0x08) == 0;
                        status.TapaAbierta = (offlineByte & 0x04) != 0;
                        status.ErrorRecuperable = (errorByte & 0x40) != 0;
                        status.TienePapel = (paperByte & 0x60) == 0;
                        Log.DebugFormat("[STATUS] {0}:{1} → DLE retry exitoso: P:{2:X2} O:{3:X2} E:{4:X2} S:{5:X2}",
                            ip, port, printerByte, offlineByte, errorByte, paperByte);
                    }
                }

                // ======[ REGLA: P:00 O:00 E:00 S:00 = ONLINE pero NO DISPONIBLE ]======
                // TCP conectó pero DLE EOT no respondió tras retry.
                // La impresora ESTÁ en la red (online=true) pero NO confirmó ser ESC/POS funcional.
                // Puede ser: adaptador de red activo con impresora apagada, print server,
                // dispositivo no-ESC/POS en puerto 9100, o modelo que no soporta DLE EOT.
                // DECISIÓN: Marcar ONLINE (está en la red) pero NO DISPONIBLE (DLE no confirmó).
                // PrintWorker verifica DLE EOT antes de imprimir — si falla ahí, reintenta.
                if (todosEnCero)
                {
                    status.Online = true;
                    status.DisponibleParaImprimir = false;
                    status.TienePapel = false;
                    status.TapaAbierta = false;
                    status.ErrorRecuperable = false;
                    status.ErrorMessage = "DLE EOT sin respuesta (TCP OK, impresora no confirmada)";
                    status.RawStatus = "P:00 O:00 E:00 S:00 [TCP_OK_NO_DLE]";

                    Log.WarnFormat("[STATUS] {0}:{1} → DLE todo cero — ONLINE pero NO DISPONIBLE (raw={2})",
                        ip, port, status.RawStatus);

                    return status;
                }

                bool dleResponseValid = (printerByte & 0x12) == 0x12
                                      || (offlineByte & 0x12) == 0x12
                                      || (errorByte & 0x12) == 0x12
                                      || (paperByte & 0x12) == 0x12;

                if (!dleResponseValid)
                {
                    // DLE respondió algo pero no cumple máscara 0x12 → respuesta ambigua.
                    // ONLINE (TCP conectó) pero NO DISPONIBLE (DLE no válido).
                    status.Online = true;
                    status.DisponibleParaImprimir = false;
                    status.TienePapel = false;
                    status.TapaAbierta = false;
                    status.ErrorRecuperable = false;
                    status.ErrorMessage = "DLE EOT respuesta invalida (TCP OK)";

                    status.RawStatus = string.Format("P:{0:X2} O:{1:X2} E:{2:X2} S:{3:X2} [TCP_OK_DLE_INVALIDO]",
                        printerByte, offlineByte, errorByte, paperByte);

                    Log.WarnFormat("[STATUS] {0}:{1} → DLE invalido — ONLINE pero NO DISPONIBLE (raw={2})",
                        ip, port, status.RawStatus);

                    return status;
                }

                // DLE respondió correctamente → usar datos reales
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

                // ======[ DLE VALIDATION ]====== (CheckSync) — con retry para TM-T20IIIL
                bool todosEnCero = printerByte == 0x00 && offlineByte == 0x00 && errorByte == 0x00 && paperByte == 0x00;

                if (todosEnCero)
                {
                    Thread.Sleep(150);
                    printerByte = SendAndReceiveByteSync(socket, DLE_EOT_PRINTER, timeoutMs);
                    offlineByte = SendAndReceiveByteSync(socket, DLE_EOT_OFFLINE, timeoutMs);
                    errorByte = SendAndReceiveByteSync(socket, DLE_EOT_ERROR, timeoutMs);
                    paperByte = SendAndReceiveByteSync(socket, DLE_EOT_PAPER, timeoutMs);

                    todosEnCero = printerByte == 0x00 && offlineByte == 0x00 && errorByte == 0x00 && paperByte == 0x00;

                    if (!todosEnCero)
                    {
                        printerReady = (printerByte & 0x08) == 0;
                        status.TapaAbierta = (offlineByte & 0x04) != 0;
                        status.ErrorRecuperable = (errorByte & 0x40) != 0;
                        status.TienePapel = (paperByte & 0x60) == 0;
                    }
                }

                // ======[ REGLA: P:00 = ONLINE pero NO DISPONIBLE ]====== (misma lógica que CheckAsync)
                if (todosEnCero)
                {
                    status.Online = true;
                    status.DisponibleParaImprimir = false;
                    status.TienePapel = false;
                    status.TapaAbierta = false;
                    status.ErrorRecuperable = false;
                    status.ErrorMessage = "DLE EOT sin respuesta (TCP OK, impresora no confirmada)";
                    status.RawStatus = "P:00 O:00 E:00 S:00 [TCP_OK_NO_DLE]";
                    return status;
                }

                bool dleResponseValid = (printerByte & 0x12) == 0x12
                                      || (offlineByte & 0x12) == 0x12
                                      || (errorByte & 0x12) == 0x12
                                      || (paperByte & 0x12) == 0x12;

                if (!dleResponseValid)
                {
                    // DLE respondió algo pero no cumple máscara 0x12 → ONLINE pero NO DISPONIBLE
                    status.Online = true;
                    status.DisponibleParaImprimir = false;
                    status.TienePapel = false;
                    status.TapaAbierta = false;
                    status.ErrorRecuperable = false;
                    status.ErrorMessage = "DLE EOT respuesta invalida (TCP OK)";
                    status.RawStatus = string.Format("P:{0:X2} O:{1:X2} E:{2:X2} S:{3:X2} [TCP_OK_DLE_INVALIDO]",
                        printerByte, offlineByte, errorByte, paperByte);
                    return status;
                }

                // DLE respondió correctamente → usar datos reales
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
            // Limpiar buffer de recepción antes de enviar (evitar datos residuales de comandos previos)
            while (socket.Available > 0)
            {
                var trash = new byte[socket.Available];
                socket.Receive(trash, 0, trash.Length, SocketFlags.None);
            }

            socket.Send(command, 0, command.Length, SocketFlags.None);

            // Esperar respuesta con timeout — microDelay para que la impresora procese
            Thread.Sleep(50);

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
