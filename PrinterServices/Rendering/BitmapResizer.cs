using System.Drawing;

namespace PrinterServices.Rendering
{
    public static class BitmapResizer
    {
        public static Bitmap ResizeIfNeeded(Bitmap bmp, int maxWidth)
        {
            if (bmp.Width <= maxWidth)
                return bmp;

            float scale = (float)maxWidth / bmp.Width;
            int newHeight = (int)(bmp.Height * scale);
            Bitmap resized = new Bitmap(maxWidth, newHeight);
            resized.SetResolution(192, 192); // Forzar resolución fija

            using (Graphics g = Graphics.FromImage(resized))
            {
                g.Clear(Color.White);
                g.DrawImage(bmp, 0, 0, maxWidth, newHeight);
            }

            return resized;
        }
    }
}
