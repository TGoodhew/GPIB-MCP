// -----------------------------------------------------------------------------
// Hpgl.Rendering - SCPI screen-image helper (.NET Framework 4.7.2).
//
// Some instruments return their screen directly as an image (PNG/BMP) via a SCPI
// query (e.g. Rigol :DISP:DATA?), with no HP-GL/PCL rendering needed (issue #10).
// This normalises those bytes to PNG and produces a SMALL inline preview: a full-
// colour screenshot cannot be pasted verbatim into an artifact (a multi-tens-of-KB
// base64 blob stalls the model - the print-hang lesson), so the inline form is a
// downscaled thumbnail bounded to a byte budget; the full-resolution PNG is saved
// to disk separately.
// -----------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Hpgl.Rendering
{
    /// <summary>Normalises instrument screen-image bytes and builds a bounded inline preview (#10).</summary>
    public static class ScreenImage
    {
        /// <summary>Candidate thumbnail widths (px), largest first; the first whose SVG fits the budget wins.</summary>
        private static readonly int[] ThumbWidths = { 800, 640, 480, 360, 280, 240, 200, 160, 128, 100, 80 };

        /// <summary>
        /// Decodes any supported instrument image (PNG, BMP, GIF, JPEG) and re-encodes it as PNG - so a
        /// bulky uncompressed BMP (Rigol returns ~1.1&nbsp;MB) becomes a compact, universally-viewable PNG.
        /// </summary>
        public static byte[] ToPng(byte[] imageBytes)
        {
            using (var bmp = Load(imageBytes))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>Pixel dimensions of an instrument image, for reporting (out-params keep callers System.Drawing-free).</summary>
        public static void Dimensions(byte[] imageBytes, out int width, out int height)
        {
            using (var bmp = Load(imageBytes)) { width = bmp.Width; height = bmp.Height; }
        }

        /// <summary>
        /// Builds a self-contained SVG embedding a DOWNSCALED PNG of the screenshot as a data URI, sized so
        /// the whole SVG stays within <paramref name="maxChars"/> (so it can be pasted verbatim into an
        /// inline artifact). Tries successively smaller widths; returns null if even the smallest will not
        /// fit, in which case the caller should fall back to the saved file + image block only.
        /// </summary>
        public static string ToBoundedInlineSvg(byte[] imageBytes, int maxChars)
        {
            using (var src = Load(imageBytes))
            {
                foreach (int width in ThumbWidths)
                {
                    if (width > src.Width && width != ThumbWidths[ThumbWidths.Length - 1]) continue; // don't upscale (except the floor)
                    string svg = TryBuild(src, Math.Min(width, src.Width), maxChars);
                    if (svg != null) return svg;
                }
                return null;
            }
        }

        private static string TryBuild(Bitmap src, int targetWidth, int maxChars)
        {
            int w = Math.Max(1, targetWidth);
            int h = Math.Max(1, (int)Math.Round(src.Height * (w / (double)src.Width)));

            // Encode the inline thumbnail as a 1-bit BLACK & WHITE PNG. A colour data-URI is base64 the
            // model must paste verbatim, and only a tiny one is reliable (~the PCL print's 2.8 KB); a
            // 1-bit image compresses like that print, so a USEFUL-size preview fits the safe budget. The
            // saved file stays full-colour, full-resolution (the result points the user to it).
            byte[] png;
            using (var scaled = Scale(src, w, h))
                png = EncodeBlackWhitePng(scaled);
            string b64 = Convert.ToBase64String(png);

            var sb = new StringBuilder(b64.Length + 200);
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
              .Append(w).Append(' ').Append(h).Append("\">\n");
            sb.Append("<image width=\"").Append(w).Append("\" height=\"").Append(h)
              .Append("\" href=\"data:image/png;base64,").Append(b64).Append("\"/>\n");
            sb.Append("</svg>");
            return sb.Length <= maxChars ? sb.ToString() : null;
        }

        private static Bitmap Scale(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        /// <summary>Luminance threshold (0-255) above which a pixel becomes white in the B&amp;W thumbnail.</summary>
        private const int BwThreshold = 96;

        /// <summary>
        /// Encodes a 1-bit black/white PNG by thresholding luminance. An instrument screen is bright
        /// elements (traces, text, grid) on a dark background, so a threshold reads well, and 1-bit
        /// compresses to a few KB - small enough to paste inline reliably.
        /// </summary>
        private static byte[] EncodeBlackWhitePng(Bitmap scaled)
        {
            int w = scaled.Width, h = scaled.Height;
            var rect = new Rectangle(0, 0, w, h);

            int[] src = new int[w * h];
            BitmapData sd = scaled.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(sd.Scan0, src, 0, src.Length); }
            finally { scaled.UnlockBits(sd); }

            using (var bw = new Bitmap(w, h, PixelFormat.Format1bppIndexed))
            {
                var pal = bw.Palette;
                pal.Entries[0] = Color.Black;
                pal.Entries[1] = Color.White;
                bw.Palette = pal;

                BitmapData dd = bw.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
                try
                {
                    int stride = dd.Stride;
                    var rows = new byte[stride * h];
                    for (int y = 0; y < h; y++)
                    {
                        int rowBase = y * stride, pxBase = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int c = src[pxBase + x];
                            int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                            int lum = (r * 299 + g * 587 + b * 114) / 1000;
                            if (lum >= BwThreshold) rows[rowBase + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        }
                    }
                    Marshal.Copy(rows, 0, dd.Scan0, rows.Length);
                }
                finally { bw.UnlockBits(dd); }

                using (var ms = new MemoryStream())
                {
                    bw.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        private static Bitmap Load(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                throw new ArgumentException("no image data", nameof(imageBytes));
            // Copy into an owned stream Bitmap can keep; new Bitmap(stream) requires the stream to stay open.
            var ms = new MemoryStream();
            ms.Write(imageBytes, 0, imageBytes.Length);
            ms.Position = 0;
            return new Bitmap(ms);
        }
    }
}
