using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrinterServices.Config;

namespace PrinterServices.Rendering
{
    /// <summary>
    /// Estima cuántos ms tarda la impresora térmica en imprimir físicamente un bitmap,
    /// basado en el alto total y la densidad de negro por fila (throttling térmico).
    ///
    /// Se usa tras el SendAsync TCP/USB para mantener el socket/worker ocupados hasta que
    /// el cabezal físicamente termine de imprimir. Evita que un siguiente job envíe ESC @
    /// (reset) mientras la impresora aún está imprimiendo el ticket anterior — lo que corta
    /// el primero a la mitad y lo cruza con el segundo.
    /// </summary>
    public static class PrintDurationEstimator
    {
        private const int BlackThreshold = 128;

        /// <summary>
        /// Calcula ms estimados de impresión física para un bitmap monocromo post-umbral.
        /// Fórmula: height * BaseMsPerRow + Σ(darkRatio_por_fila) * ExtraMsPerBlackRow.
        /// Clamp a [MinMs, MaxMs] desde ConfigManager. Retorna 0 si bmp es null.
        /// </summary>
        public static int EstimateMs(Bitmap bmp)
        {
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return 0;

            var cfg = ConfigManager.Instance;
            int baseMsPerRow = cfg.GetInt("PostPrintWaitBaseMsPerRow", 2);
            int extraMsPerBlackRow = cfg.GetInt("PostPrintWaitExtraMsPerBlackRow", 10);
            int minMs = cfg.GetInt("PostPrintWaitMinMs", 100);
            int maxMs = cfg.GetInt("PostPrintWaitMaxMs", 4000);

            double accumulatedDarkRows = SumRowDarknessRatios(bmp);

            double ms = (double)bmp.Height * baseMsPerRow
                      + accumulatedDarkRows * extraMsPerBlackRow;

            int result = (int)Math.Round(ms);
            if (result < minMs) result = minMs;
            if (result > maxMs) result = maxMs;
            return result;
        }

        /// <summary>
        /// Recorre filas del bitmap vía LockBits (rápido) y acumula la proporción de pixels
        /// "negros" (luma &lt; 128) por fila. Soporta 32bpp y 24bpp; si no, fallback a GetPixel.
        /// </summary>
        private static double SumRowDarknessRatios(Bitmap bmp)
        {
            // Fast path: LockBits si el formato es 32bpp o 24bpp
            if (bmp.PixelFormat == PixelFormat.Format32bppArgb
                || bmp.PixelFormat == PixelFormat.Format32bppRgb
                || bmp.PixelFormat == PixelFormat.Format32bppPArgb
                || bmp.PixelFormat == PixelFormat.Format24bppRgb)
            {
                return SumRowDarknessRatiosFast(bmp);
            }
            return SumRowDarknessRatiosSlow(bmp);
        }

        private static double SumRowDarknessRatiosFast(Bitmap bmp)
        {
            int width = bmp.Width;
            int height = bmp.Height;
            int bytesPerPixel = Image.GetPixelFormatSize(bmp.PixelFormat) / 8;

            BitmapData data = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                bmp.PixelFormat);

            try
            {
                double total = 0;
                byte[] rowBuf = new byte[Math.Abs(data.Stride)];
                IntPtr scan0 = data.Scan0;
                int stride = data.Stride;

                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(scan0 + y * stride, rowBuf, 0, rowBuf.Length);
                    int blackCount = 0;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = x * bytesPerPixel;
                        // BGR(A) en GDI+. Luma aproximado: (R+G+B)/3
                        int b = rowBuf[idx];
                        int g = rowBuf[idx + 1];
                        int r = rowBuf[idx + 2];
                        int luma = (r + g + b) / 3;
                        if (luma < BlackThreshold) blackCount++;
                    }
                    total += (double)blackCount / width;
                }
                return total;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static double SumRowDarknessRatiosSlow(Bitmap bmp)
        {
            int width = bmp.Width;
            int height = bmp.Height;
            double total = 0;
            for (int y = 0; y < height; y++)
            {
                int blackCount = 0;
                for (int x = 0; x < width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    int luma = (c.R + c.G + c.B) / 3;
                    if (luma < BlackThreshold) blackCount++;
                }
                total += (double)blackCount / width;
            }
            return total;
        }
    }
}
