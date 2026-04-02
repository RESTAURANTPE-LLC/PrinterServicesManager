using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace PrinterServices.Drivers
{
    public enum Alignment
    {
        Left = 0,
        Center = 1,
        Right = 2
    }

    public enum FontSize
    {
        Normal,
        Small,
        Medium,
        Large,
        DoubleWidth,
        DoubleHeight,
        DoubleWidthHeight
    }

    public class EscPosCommandBuilder
    {
        private readonly List<byte[]> _commands;
        private readonly IPrinterDriver _driver;
        private readonly Encoding _encoding;

        public EscPosCommandBuilder(IPrinterDriver driver)
        {
            _driver = driver;
            _commands = new List<byte[]>();

            try
            {
                _encoding = Encoding.GetEncoding(1252);
            }
            catch
            {
                _encoding = Encoding.GetEncoding(437);
            }
        }

        public EscPosCommandBuilder Init()
        {
            _commands.Add(_driver.GetInitSequence());
            return this;
        }

        public EscPosCommandBuilder Text(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                // Set codepage Windows-1252 (ESC t 16)
                _commands.Add(new byte[] { 0x1C, 0x2E }); // FS .
                _commands.Add(new byte[] { 0x1B, 0x74, 0x10 }); // ESC t 16
                _commands.Add(_encoding.GetBytes(text));
            }
            return this;
        }

        public EscPosCommandBuilder NewLine()
        {
            _commands.Add(new byte[] { 0x0A });
            return this;
        }

        public EscPosCommandBuilder Feed(int lines)
        {
            if (lines <= 0) lines = 1;
            // ESC d n — Print and feed n lines
            _commands.Add(new byte[] { 0x1B, 0x64, (byte)Math.Min(lines, 255) });
            return this;
        }

        public EscPosCommandBuilder SetAlignment(Alignment alignment)
        {
            // ESC a n
            _commands.Add(new byte[] { 0x1B, 0x61, (byte)alignment });
            return this;
        }

        public EscPosCommandBuilder SetBold(bool enabled)
        {
            // ESC E n
            _commands.Add(new byte[] { 0x1B, 0x45, (byte)(enabled ? 1 : 0) });
            return this;
        }

        public EscPosCommandBuilder SetUnderline(int thickness)
        {
            // ESC - n  (0=off, 1=1dot, 2=2dot)
            if (thickness < 0 || thickness > 2) thickness = 0;
            _commands.Add(new byte[] { 0x1B, 0x2D, (byte)thickness });
            return this;
        }

        public EscPosCommandBuilder SetFontSize(FontSize size)
        {
            // ESC ! n — Select print mode
            byte mode;
            switch (size)
            {
                case FontSize.Small:
                    mode = 0x01; // Font B
                    break;
                case FontSize.Large:
                    mode = 0x40; // Double height (bit 4) — 0x10 | Double width (bit 5) — 0x20 = 0x30
                    break;
                case FontSize.DoubleWidth:
                    mode = 0x20; // MODE_DOUBLE_WIDTH
                    break;
                case FontSize.DoubleHeight:
                    mode = 0x10; // MODE_DOUBLE_HEIGHT
                    break;
                case FontSize.DoubleWidthHeight:
                    mode = 0x30; // Both
                    break;
                case FontSize.Medium:
                case FontSize.Normal:
                default:
                    mode = 0x00;
                    break;
            }
            _commands.Add(new byte[] { 0x1B, 0x21, mode });
            return this;
        }

        public EscPosCommandBuilder SetLetterSize(string tamanioLetra)
        {
            const byte SpacingDefault = 56;
            const byte SpacingSmall = 52;

            // Reset line spacing
            _commands.Add(new byte[] { 0x1B, 0x33, SpacingDefault });

            switch (tamanioLetra)
            {
                case "1":
                    // Font B + small spacing
                    _commands.Add(new byte[] { 0x1B, 0x21, 0x01 });
                    _commands.Add(new byte[] { 0x1B, 0x33, SpacingSmall });
                    break;
                case "2":
                    // Font A + small spacing
                    _commands.Add(new byte[] { 0x1B, 0x21, 0x00 });
                    _commands.Add(new byte[] { 0x1B, 0x33, SpacingSmall });
                    break;
                case "3":
                    // Large font, default spacing
                    _commands.Add(new byte[] { 0x1B, 0x21, 0x40 });
                    break;
                default:
                    _commands.Add(new byte[] { 0x1B, 0x21, 0x00 });
                    break;
            }
            return this;
        }

        public EscPosCommandBuilder SetLineSpacing(int dots)
        {
            // ESC 3 n — Set line spacing to n dots
            _commands.Add(new byte[] { 0x1B, 0x33, (byte)Math.Min(dots, 255) });
            return this;
        }

        public EscPosCommandBuilder ResetFormatting()
        {
            // Reset: normal font, left align, no bold, no underline
            _commands.Add(new byte[] { 0x1B, 0x21, 0x00 }); // Normal mode
            _commands.Add(new byte[] { 0x1B, 0x61, 0x00 }); // Left align
            _commands.Add(new byte[] { 0x1B, 0x45, 0x00 }); // Bold off
            _commands.Add(new byte[] { 0x1B, 0x2D, 0x00 }); // Underline off
            return this;
        }

        public EscPosCommandBuilder PrintQrCode(string data, int moduleSize, int errorCorrection)
        {
            if (string.IsNullOrEmpty(data)) return this;

            byte[] qrData = _encoding.GetBytes(data);
            int storeLen = qrData.Length + 3;
            byte storePL = (byte)(storeLen % 256);
            byte storePH = (byte)(storeLen / 256);

            if (moduleSize < 1) moduleSize = 4;
            if (moduleSize > 16) moduleSize = 16;
            if (errorCorrection < 0) errorCorrection = 1;
            if (errorCorrection > 3) errorCorrection = 3;

            // Select model 2
            _commands.Add(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 });
            // Set module size
            _commands.Add(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)moduleSize });
            // Set error correction
            _commands.Add(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, (byte)(0x30 + errorCorrection) });
            // Store data
            _commands.Add(new byte[] { 0x1D, 0x28, 0x6B, storePL, storePH, 0x31, 0x50, 0x30 });
            _commands.Add(qrData);
            // Print QR
            _commands.Add(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 });

            return this;
        }

        public EscPosCommandBuilder PrintBarcode(string data, int type, int height, int width)
        {
            if (string.IsNullOrEmpty(data)) return this;

            if (height < 1) height = 80;
            if (height > 255) height = 255;
            if (width < 2) width = 3;
            if (width > 6) width = 6;

            // GS h n — Set barcode height
            _commands.Add(new byte[] { 0x1D, 0x68, (byte)height });
            // GS w n — Set barcode width
            _commands.Add(new byte[] { 0x1D, 0x77, (byte)width });
            // GS H n — HRI position below
            _commands.Add(new byte[] { 0x1D, 0x48, 0x02 });
            // GS k m n d1...dn
            byte[] barcodeData = _encoding.GetBytes(data);
            _commands.Add(new byte[] { 0x1D, 0x6B, (byte)type, (byte)barcodeData.Length });
            _commands.Add(barcodeData);

            return this;
        }

        public EscPosCommandBuilder OpenCashDrawer()
        {
            _commands.Add(_driver.GetCashDrawerCommand());
            return this;
        }

        public EscPosCommandBuilder Cut(CutType cutType)
        {
            _commands.Add(_driver.GetCutCommand(cutType));
            return this;
        }

        public EscPosCommandBuilder AddBitmapFromImage(Bitmap bmp)
        {
            if (bmp == null) return this;

            BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format1bppIndexed);

            int width = bmp.Width;
            int height = bmp.Height;
            int bytesPerRow = (width + 7) / 8;

            // GS v 0 m xL xH yL yH d1...dk
            byte xL = (byte)(bytesPerRow % 256);
            byte xH = (byte)(bytesPerRow / 256);
            byte yL = (byte)(height % 256);
            byte yH = (byte)(height / 256);

            _commands.Add(new byte[] { 0x1D, 0x76, 0x30, 0x00, xL, xH, yL, yH });

            int stride = bmpData.Stride;
            IntPtr ptr = bmpData.Scan0;
            byte[] data = new byte[stride * height];
            Marshal.Copy(ptr, data, 0, data.Length);

            for (int i = 0; i < height; i++)
            {
                byte[] rowBytes = new byte[bytesPerRow];
                for (int j = 0; j < bytesPerRow; j++)
                {
                    byte b = 0x00;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int x = j * 8 + bit;
                        if (x < width)
                        {
                            int index = i * stride + (x / 8);
                            bool black = (data[index] & (0x80 >> (x % 8))) == 0;
                            if (black)
                            {
                                b |= (byte)(0x80 >> bit);
                            }
                        }
                    }
                    rowBytes[j] = b;
                }
                _commands.Add(rowBytes);
            }

            bmp.UnlockBits(bmpData);
            return this;
        }

        /// <summary>
        /// Envía bitmap usando ESC * (bit image mode 33 = 24-dot double density).
        /// Procesa la imagen en bandas horizontales de 24 píxeles de alto.
        /// Más compatible que GS v 0 con emulaciones no-Epson (CUSTOM/POS, Star, etc.)
        /// porque cada banda es un comando independiente y la impresora procesa banda por banda.
        /// </summary>
        public EscPosCommandBuilder AddBitmapEscAsterisk(Bitmap bmp)
        {
            if (bmp == null) return this;

            BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format1bppIndexed);

            int width = bmp.Width;
            int height = bmp.Height;
            int stride = bmpData.Stride;
            IntPtr ptr = bmpData.Scan0;
            byte[] data = new byte[stride * height];
            Marshal.Copy(ptr, data, 0, data.Length);

            // ESC 3 24 — Setear line spacing a exactamente 24 dots (elimina gaps entre bandas)
            _commands.Add(new byte[] { 0x1B, 0x33, 24 });

            // ESC * m nL nH d1...dk — modo 33 = 24-dot double density
            // Procesar en bandas de 24 líneas de alto
            byte nL = (byte)(width % 256);
            byte nH = (byte)(width / 256);

            for (int bandTop = 0; bandTop < height; bandTop += 24)
            {
                // Comando ESC * 33 nL nH
                _commands.Add(new byte[] { 0x1B, 0x2A, 33, nL, nH });

                // 3 bytes por columna (24 dots verticales)
                byte[] bandData = new byte[width * 3];
                for (int x = 0; x < width; x++)
                {
                    for (int dot = 0; dot < 24; dot++)
                    {
                        int y = bandTop + dot;
                        if (y >= height) break;

                        int byteIndex = y * stride + (x / 8);
                        bool black = (data[byteIndex] & (0x80 >> (x % 8))) == 0;
                        if (black)
                        {
                            // dot 0-7 → byte 0, dot 8-15 → byte 1, dot 16-23 → byte 2
                            bandData[x * 3 + dot / 8] |= (byte)(0x80 >> (dot % 8));
                        }
                    }
                }
                _commands.Add(bandData);

                // Line feed después de cada banda para avanzar exactamente 24 dots
                _commands.Add(new byte[] { 0x0A });
            }

            // ESC 2 — Restaurar line spacing al default de la impresora
            _commands.Add(new byte[] { 0x1B, 0x32 });

            bmp.UnlockBits(bmpData);
            return this;
        }

        public EscPosCommandBuilder RawBytes(byte[] data)
        {
            if (data != null && data.Length > 0)
            {
                _commands.Add(data);
            }
            return this;
        }

        public byte[] Build()
        {
            int totalLength = 0;
            foreach (var cmd in _commands)
            {
                totalLength += cmd.Length;
            }

            var result = new byte[totalLength];
            int offset = 0;
            foreach (var cmd in _commands)
            {
                Buffer.BlockCopy(cmd, 0, result, offset, cmd.Length);
                offset += cmd.Length;
            }

            return result;
        }
    }
}
