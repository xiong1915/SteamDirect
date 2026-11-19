using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace SteamDirect
{
    // One small image shared by the overview banner. Native text controls use solid surfaces.
    internal static class Wallpaper
    {
        private static readonly Lazy<Bitmap> artwork = new Lazy<Bitmap>(LoadBanner);

        private static Bitmap LoadBanner()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamDirect.Wallpaper"))
            {
                if (stream == null) return null;
                using (var original = Image.FromStream(stream))
                    return new Bitmap(original, 800, 450);
            }
        }

        internal static void PaintBanner(Graphics graphics, Rectangle bounds, GraphicsPath outline, Color surface, int dpi)
        {
            int width = Math.Min(bounds.Width / 2, (int)(540 * dpi / 96F));
            if (width < 2 || bounds.Height < 2) return;
            var banner = new Rectangle(bounds.Right - width, bounds.Top, width, bounds.Height);
            var image = artwork.Value;
            if (image == null) return;

            float scale = Math.Max(banner.Width / (float)image.Width, banner.Height / (float)image.Height);
            float sourceWidth = banner.Width / scale;
            float sourceHeight = banner.Height / scale;
            var source = new RectangleF((image.Width - sourceWidth) / 2,
                (image.Height - sourceHeight) * .55F, sourceWidth, sourceHeight);

            var state = graphics.Save();
            try
            {
                graphics.SetClip(outline, CombineMode.Intersect);
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.DrawImage(image, banner, source, GraphicsUnit.Pixel);
                var veilBounds = Rectangle.Inflate(banner, 2, 0);
                using (var veil = new LinearGradientBrush(veilBounds, surface, Color.FromArgb(26, surface), 0F))
                {
                    veil.Blend = new Blend
                    {
                        Positions = new[] { 0F, .14F, .65F, 1F },
                        Factors = new[] { 0F, .05F, .83F, 1F }
                    };
                    graphics.FillRectangle(veil, veilBounds);
                }
            }
            finally { graphics.Restore(state); }
        }
    }
}
