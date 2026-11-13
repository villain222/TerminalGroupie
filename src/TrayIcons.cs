using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TerminalGroupie
{
    /// <summary>
    /// Generates the tray icons at runtime: a terminal glyph tinted GREEN for a
    /// standard instance and RED for an elevated instance, so the two kinds are
    /// distinguishable in the system tray. Emits a multi-size ICO (16–48 px, PNG
    /// entries) and exposes a WPF-ready ImageSource for window title bars.
    /// </summary>
    public static class TrayIcons
    {
        private static readonly int[] Sizes = { 16, 24, 32, 48 };
        private static readonly Color StandardColor = Color.FromArgb(62, 176, 74);  // green
        private static readonly Color ElevatedColor = Color.FromArgb(229, 67, 54);  // red

        private static Icon? _standard;
        private static Icon? _elevated;

        /// <summary>Green terminal icon (standard instance).</summary>
        public static Icon Standard => _standard ??= Build(StandardColor);

        /// <summary>Red terminal icon (elevated instance).</summary>
        public static Icon Elevated => _elevated ??= Build(ElevatedColor);

        /// <summary>The icon for the given instance kind.</summary>
        public static Icon For(bool elevated) => elevated ? Elevated : Standard;

        /// <summary>The icon as a WPF ImageSource (for window title bars).</summary>
        public static System.Windows.Media.ImageSource ToImageSource(bool elevated)
        {
            using (var ms = new MemoryStream())
            {
                For(elevated).Save(ms);
                ms.Position = 0;

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }

        private static Icon Build(Color accent)
        {
            // Render every size to PNG first, then assemble the ICO around them.
            var pngs = new List<byte[]>(Sizes.Length);
            foreach (int size in Sizes)
            {
                using var bmp = Render(size, accent);
                using var m = new MemoryStream();
                bmp.Save(m, ImageFormat.Png);
                pngs.Add(m.ToArray());
            }

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0);             // reserved
                w.Write((short)1);             // type: icon
                w.Write((short)Sizes.Length);  // image count

                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    int size = Sizes[i];
                    w.Write((byte)(size > 255 ? 0 : size)); // width  (0 = 256)
                    w.Write((byte)(size > 255 ? 0 : size)); // height
                    w.Write((byte)0);                       // palette entries
                    w.Write((byte)0);                       // reserved
                    w.Write((short)1);                      // color planes
                    w.Write((short)32);                     // bits per pixel
                    w.Write(pngs[i].Length);                // data size
                    w.Write(offset);                        // data offset
                    offset += pngs[i].Length;
                }

                foreach (var png in pngs)
                {
                    w.Write(png);
                }
            }

            // The Icon owns the stream for its lifetime (process-lifetime cache).
            return new Icon(new MemoryStream(ms.ToArray()));
        }

        private static Bitmap Render(int size, Color accent)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font("Consolas", size * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var textBrush = new SolidBrush(accent))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                float pad = size * 0.07f;
                float radius = size * 0.20f;
                var body = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                using (var path = RoundedRect(body, radius))
                using (var bg = new SolidBrush(Color.FromArgb(28, 28, 30)))
                {
                    g.FillPath(bg, path);
                }

                float border = Math.Max(1f, size * 0.07f);
                var ring = new RectangleF(
                    pad + border / 2, pad + border / 2,
                    size - 2 * pad - border, size - 2 * pad - border);
                using (var path = RoundedRect(ring, Math.Max(1f, radius - border / 2)))
                using (var pen = new Pen(accent, border))
                {
                    g.DrawPath(pen, path);
                }

                string prompt = ">_";
                SizeF t = g.MeasureString(prompt, font);
                float x = (size - t.Width) / 2f + size * 0.02f;
                float y = (size - t.Height) / 2f + size * 0.03f;
                g.DrawString(prompt, font, textBrush, x, y);
            }
            return bmp;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
