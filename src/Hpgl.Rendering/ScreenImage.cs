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

            // Quantize the thumbnail to a <=256-colour indexed PNG: a fraction of the 24-bit size, so a
            // useful preview fits the small paste-verbatim budget. (The saved file stays full colour.)
            byte[] png;
            using (var scaled = Scale(src, w, h))
                png = MedianCutQuantizer.EncodePng(scaled, 256);
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
