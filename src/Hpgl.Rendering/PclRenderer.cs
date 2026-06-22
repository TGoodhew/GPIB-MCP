// -----------------------------------------------------------------------------
// Hpgl.Rendering - HP PCL raster renderer (.NET Framework 4.7.2).
//
// Public front-end for PCL "print" hardcopy, complementing HpglRenderer's HP-GL/2
// "plot" vector path. Decodes a PCL byte stream (see PclRasterDecoder) to a
// monochrome dot matrix and rasterizes it onto the requested canvas, reusing
// HpglRenderOptions (Width/Height/Background/PenColors) so the two renderers share
// one option surface. A print job that is actually an embedded HP-GL/2 picture is
// handed straight to HpglRenderer.
// -----------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Hpgl.Rendering
{
    /// <summary>Renders an HP PCL print/raster stream to a raster (<see cref="Bitmap"/> or PNG bytes).</summary>
    public static class PclRenderer
    {
        /// <summary>Renders a PCL byte stream to a <see cref="Bitmap"/>. Caller owns/disposes the bitmap.</summary>
        public static Bitmap RenderToBitmap(byte[] pcl, HpglRenderOptions options = null)
        {
            options = options ?? new HpglRenderOptions();
            var image = PclRasterDecoder.Decode(pcl ?? Array.Empty<byte>());

            // A print job that is purely an embedded HP-GL/2 picture (no raster) belongs to the vector path.
            if (image.IsEmpty && image.EmbeddedHpgl != null)
                return HpglRenderer.RenderToBitmap(image.EmbeddedHpgl, options);

            var bmp = new Bitmap(options.Width, options.Height, PixelFormat.Format32bppArgb);
            Color background = options.ResolveBackground();
            Color ink = options.ResolvePen(1);

            using (var g = Graphics.FromImage(bmp))
                g.Clear(background);

            if (!image.IsEmpty)
                Blit(bmp, image, background, ink, options.Margin);

            return bmp;
        }

        /// <summary>Renders a PCL byte stream and encodes the result as a PNG byte array.</summary>
        public static byte[] RenderToPng(byte[] pcl, HpglRenderOptions options = null)
        {
            using (var bmp = RenderToBitmap(pcl, options))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Renders a PCL stream to a self-contained SVG document that embeds the raster as a data URI. PCL
        /// output is inherently raster, so - unlike the HP-GL/2 path - there is no vector form; wrapping
        /// the bitmap in an SVG lets it ride the same inline-artifact display path as a plotted capture.
        ///
        /// The embedded PNG is the NATIVE dot matrix (one pixel per dot) as a 1-bit image, NOT the upscaled
        /// canvas - so the data URI stays small (comparable to a plotted SVG). This matters because the
        /// inline path asks the model to reproduce the SVG verbatim into an artifact; a multi-tens-of-KB
        /// base64 blob stalls that, whereas a native 1-bit raster is a few KB. A viewBox + width/height
        /// scales it up to fill the artifact, and image-rendering:pixelated keeps the dots crisp.
        /// </summary>
        public static string RenderToSvg(byte[] pcl, HpglRenderOptions options = null)
        {
            options = options ?? new HpglRenderOptions();
            var image = PclRasterDecoder.Decode(pcl ?? Array.Empty<byte>());
            if (image.IsEmpty && image.EmbeddedHpgl != null)
                return HpglRenderer.RenderToSvg(image.EmbeddedHpgl, options);

            int vw = image.IsEmpty ? options.Width : image.Width;
            int vh = image.IsEmpty ? options.Height : image.Height;
            string b64 = image.IsEmpty
                ? Convert.ToBase64String(RenderToPng(pcl, options))
                : Convert.ToBase64String(RenderNativePng(image, options.ResolveBackground(), options.ResolvePen(1)));

            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
              .Append(vw).Append(' ').Append(vh).Append("\">\n");
            sb.Append("<image width=\"").Append(vw).Append("\" height=\"").Append(vh)
              .Append("\" image-rendering=\"pixelated\" href=\"data:image/png;base64,").Append(b64).Append("\"/>\n");
            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>
        /// Encodes the decoded dot matrix as a compact native-resolution 1-bit PNG (palette: background,
        /// ink). The PCL row bytes are already MSB-first 1-bpp, so they map straight onto the bitmap rows.
        /// </summary>
        private static byte[] RenderNativePng(PclRasterImage image, Color background, Color ink)
        {
            using (var bmp = new Bitmap(image.Width, image.Height, PixelFormat.Format1bppIndexed))
            {
                var palette = bmp.Palette;
                palette.Entries[0] = background; // bit 0 = background
                palette.Entries[1] = ink;        // bit 1 = lit dot
                bmp.Palette = palette;

                var rect = new Rectangle(0, 0, image.Width, image.Height);
                BitmapData bits = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
                try
                {
                    int stride = bits.Stride;                 // >= RowBytes, padded to 4 bytes
                    var buffer = new byte[stride * image.Height];
                    for (int y = 0; y < image.Height; y++)
                    {
                        byte[] row = (byte[])image.Rows[y];
                        Array.Copy(row, 0, buffer, y * stride, Math.Min(stride, row.Length));
                    }
                    Marshal.Copy(buffer, 0, bits.Scan0, buffer.Length);
                }
                finally { bmp.UnlockBits(bits); }

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// Heuristic: does this byte stream look like PCL rather than plain HP-GL/2? True when it carries
        /// a PCL raster/control escape (<c>ESC*</c>, <c>ESC&amp;</c>, <c>ESC%</c>) or a printer reset
        /// (<c>ESC E</c>). HP-GL/2 plot captures are plain ASCII mnemonics and contain none of these.
        /// </summary>
        public static bool LooksLikePcl(byte[] data)
        {
            if (data == null) return false;
            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] != 0x1B) continue;
                byte c = data[i + 1];
                if (c == '*' || c == '&' || c == '%' || c == 'E') return true;
            }
            return false;
        }

        /// <summary>
        /// Scales the decoded dot matrix to fit the canvas (aspect-preserving, centred, nearest-neighbour)
        /// and paints set dots in <paramref name="ink"/> over <paramref name="background"/>.
        /// </summary>
        private static void Blit(Bitmap bmp, PclRasterImage image, Color background, Color ink, int margin)
        {
            int cw = bmp.Width, ch = bmp.Height;
            int availW = Math.Max(1, cw - 2 * margin);
            int availH = Math.Max(1, ch - 2 * margin);

            double scale = Math.Min(availW / (double)image.Width, availH / (double)image.Height);
            if (scale <= 0 || double.IsInfinity(scale)) scale = 1;
            int dstW = Math.Max(1, (int)Math.Round(image.Width * scale));
            int dstH = Math.Max(1, (int)Math.Round(image.Height * scale));
            int dstX = (cw - dstW) / 2;
            int dstY = (ch - dstH) / 2;

            int inkArgb = ink.ToArgb();
            var rect = new Rectangle(0, 0, cw, ch);
            BitmapData bits = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bits.Stride;
                var buffer = new int[stride / 4 * ch];
                Marshal.Copy(bits.Scan0, buffer, 0, buffer.Length);

                for (int y = 0; y < dstH; y++)
                {
                    int srcY = (int)((y + 0.5) / scale);
                    if (srcY >= image.Height) srcY = image.Height - 1;
                    byte[] srcRow = (byte[])image.Rows[srcY];
                    int rowBase = (dstY + y) * (stride / 4);
                    for (int x = 0; x < dstW; x++)
                    {
                        int srcX = (int)((x + 0.5) / scale);
                        if (srcX >= image.Width) srcX = image.Width - 1;
                        int b = srcX >> 3;
                        if (b < srcRow.Length && (srcRow[b] & (0x80 >> (srcX & 7))) != 0)
                            buffer[rowBase + dstX + x] = inkArgb;
                    }
                }

                Marshal.Copy(buffer, 0, bits.Scan0, buffer.Length);
            }
            finally
            {
                bmp.UnlockBits(bits);
            }
        }
    }
}
