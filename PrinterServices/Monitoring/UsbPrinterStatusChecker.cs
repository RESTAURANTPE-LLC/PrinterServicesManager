using System;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Transport;

namespace PrinterServices.Monitoring
{
    /// <summary>
    /// Verifica estado de impresoras USB usando DLE EOT (mismos comandos que red).
    /// Análogo a PrinterStatusChecker pero para dispositivos USB.
    /// PASO 1: Verificar si el dispositivo está conectado (SetupAPI → enumeración rápida)
    /// PASO 2: Abrir dispositivo y enviar DLE EOT para estado detallado (papel, tapa, error)
    /// </summary>
    public static class UsbPrinterStatusChecker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(UsbPrinterStatusChecker));

        // Mismos comandos DLE EOT que para red — el protocolo ESC/POS es idéntico
        // independientemente del transporte (TCP, USB, Serial)
        private static readonly byte[] DLE_EOT_PRINTER = { 0x10, 0x04, 0x01 }; // Printer status
        private static readonly byte[] DLE_EOT_OFFLINE = { 0x10, 0x04, 0x02 }; // Offline causes
        private static readonly byte[] DLE_EOT_ERROR   = { 0x10, 0x04, 0x03 }; // Error status
        private static readonly byte[] DLE_EOT_PAPER   = { 0x10, 0x04, 0x04 }; // Paper sensor

        /// <summary>
        /// Verifica estado completo de una impresora USB.
        /// Primero verifica conexión física (SetupAPI), luego consulta estado detallado (DLE EOT).
        /// </summary>
        /// <param name="usbUniqueKey">Identidad inmutable (VID+PID+Serial)</param>
        /// <param name="timeoutMs">Timeout para lectura de respuesta DLE EOT</param>
        /// <param name="ct">Token de cancelación</param>
        /// <returns>PrinterStatus con estado real del dispositivo</returns>
        public static async Task<PrinterStatus> CheckAsync(
            string usbUniqueKey, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(usbUniqueKey))
                return PrinterStatus.Offline("USB key vacía");

            // ═══════════════════════════════════════════════════════════════════
            // PASO 1: ¿Está físicamente conectado? (rápido, no abre dispositivo)
            // Análogo a verificar si responde a ping/TCP en impresoras de red
            // ═══════════════════════════════════════════════════════════════════
            var device = UsbDeviceEnumerator.FindByUniqueKey(usbUniqueKey);
            if (device == null)
            {
                return PrinterStatus.Offline("Impresora USB desconectada");
            }

            // ═══════════════════════════════════════════════════════════════════
            // PASO 2: Abrir dispositivo directamente con el DevicePath ya resuelto
            // Evita doble enumeración (FindByUniqueKey ya lo encontró arriba)
            // ═══════════════════════════════════════════════════════════════════
            using (var transport = new UsbTransportDirect(device.DevicePath))
            {
                try
                {
                    await transport.ConnectAsync(ct);

                    var status = new PrinterStatus
                    {
                        Online = true,
                        TienePapel = true,
                        ResolvedUsbDevicePath = device.DevicePath // Para que StatusMonitor detecte cambio de puerto
                    };

                    // DLE EOT 1 — Printer status
                    await transport.SendAsync(DLE_EOT_PRINTER, ct);
                    byte[] resp1 = await transport.ReceiveAsync(1, timeoutMs, ct);
                    byte printerByte = resp1.Length > 0 ? resp1[0] : (byte)0x00;
                    bool printerReady = (printerByte & 0x08) == 0;

                    // DLE EOT 2 — Offline causes (cover open, etc.)
                    await transport.SendAsync(DLE_EOT_OFFLINE, ct);
                    byte[] resp2 = await transport.ReceiveAsync(1, timeoutMs, ct);
                    byte offlineByte = resp2.Length > 0 ? resp2[0] : (byte)0x00;
                    status.TapaAbierta = (offlineByte & 0x04) != 0;

                    // DLE EOT 3 — Error status
                    await transport.SendAsync(DLE_EOT_ERROR, ct);
                    byte[] resp3 = await transport.ReceiveAsync(1, timeoutMs, ct);
                    byte errorByte = resp3.Length > 0 ? resp3[0] : (byte)0x00;
                    status.ErrorRecuperable = (errorByte & 0x40) != 0;

                    // DLE EOT 4 — Paper sensor
                    await transport.SendAsync(DLE_EOT_PAPER, ct);
                    byte[] resp4 = await transport.ReceiveAsync(1, timeoutMs, ct);
                    byte paperByte = resp4.Length > 0 ? resp4[0] : (byte)0x00;
                    status.TienePapel = (paperByte & 0x60) == 0;

                    // Calcular disponibilidad: todas las condiciones deben ser OK
                    status.DisponibleParaImprimir = printerReady
                        && !status.TapaAbierta && status.TienePapel;

                    status.RawStatus = string.Format("P:{0:X2} O:{1:X2} E:{2:X2} S:{3:X2}",
                        printerByte, offlineByte, errorByte, paperByte);

                    Log.DebugFormat("[USB-STATUS] {0} → online={1} disponible={2} papel={3} tapa={4} raw={5}",
                        usbUniqueKey, status.Online, status.DisponibleParaImprimir,
                        status.TienePapel, status.TapaAbierta, status.RawStatus);

                    return status;
                }
                catch (Exception ex)
                {
                    // El dispositivo está conectado (SetupAPI lo encontró) pero no se pudo abrir/consultar
                    // Puede ser: otra app tiene el dispositivo abierto, error de permisos, etc.
                    Log.DebugFormat("[USB-STATUS] {0} → Error: {1}", usbUniqueKey, ex.Message);
                    return PrinterStatus.Offline("Error USB: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Versión síncrona para compatibilidad con CheckPrinterNow().
        /// </summary>
        public static PrinterStatus CheckSync(string usbUniqueKey, int timeoutMs)
        {
            try
            {
                return CheckAsync(usbUniqueKey, timeoutMs, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return PrinterStatus.Offline("Error USB sync: " + ex.Message);
            }
        }
    }
}
