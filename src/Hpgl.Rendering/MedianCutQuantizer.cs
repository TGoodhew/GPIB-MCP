// -----------------------------------------------------------------------------
// Hpgl.Rendering - median-cut colour quantizer (.NET Framework 4.7.2).
//
// Reduces a full-colour bitmap to a <=256-colour palette and encodes it as an
// 8-bit indexed PNG. Used only for the INLINE screenshot thumbnail (#10): an
// indexed PNG is a fraction of the 24-bit size, so a useful preview fits the
// small "paste-verbatim" byte budget without stalling the model. The saved
// full-resolution screenshot keeps its original colour depth.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Hpgl.Rendering
{
    /// <summary>Median-cut quantization to a &lt;=256-colour indexed PNG (for compact inline previews).</summary>
    internal static class MedianCutQuantizer
    {
        public static byte[] EncodePng(Bitmap src, int maxColors = 256)
        {
            if (maxColors < 2) maxColors = 2;
            if (maxColors > 256) maxColors = 256;

            int w = src.Width, h = src.Height;
            int[] pixels = ReadPixels(src, w, h);          // 0x00RRGGBB per pixel

            Color[] palette = BuildPalette(pixels, maxColors);
            byte[] indices = MapToPalette(pixels, palette);

            using (var dst = new Bitmap(w, h, PixelFormat.Format8bppIndexed))
            {
                ColorPalette cp = dst.Palette;
                for (int i = 0; i < 256; i++) cp.Entries[i] = i < palette.Length ? palette[i] : Color.Black;
                dst.Palette = cp;

                var rect = new Rectangle(0, 0, w, h);
                BitmapData bd = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                try
                {
                    int stride = bd.Stride;
                    var rows = new byte[stride * h];
                    for (int y = 0; y < h; y++)
                        Array.Copy(indices, y * w, rows, y * stride, w);
                    Marshal.Copy(rows, 0, bd.Scan0, rows.Length);
                }
                finally { dst.UnlockBits(bd); }

                using (var ms = new MemoryStream())
                {
                    dst.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        private static int[] ReadPixels(Bitmap src, int w, int h)
        {
            var pixels = new int[w * h];
            using (var bmp32 = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp32)) g.DrawImageUnscaled(src, 0, 0);
                var rect = new Rectangle(0, 0, w, h);
                BitmapData bd = bmp32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var buf = new int[w * h];
                    Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                    for (int i = 0; i < buf.Length; i++) pixels[i] = buf[i] & 0x00FFFFFF; // drop alpha
                }
                finally { bmp32.UnlockBits(bd); }
            }
            return pixels;
        }

        /// <summary>A range of the working colour array that becomes one palette entry.</summary>
        private struct Box { public int Start, End; }

        private static Color[] BuildPalette(int[] pixels, int maxColors)
        {
            var cols = (int[])pixels.Clone();
            var boxes = new List<Box> { new Box { Start = 0, End = cols.Length } };

            while (boxes.Count < maxColors)
            {
                int target = -1, bestRange = -1, bestChannel = 0;
                for (int b = 0; b < boxes.Count; b++)
                {
                    if (boxes[b].End - boxes[b].Start < 2) continue;
                    int range = ChannelRange(cols, boxes[b], out int channel);
                    if (range > bestRange) { bestRange = range; target = b; bestChannel = channel; }
                }
                if (target < 0 || bestRange <= 0) break; // nothing splittable

                Box box = boxes[target];
                int shift = bestChannel * 8;
                SortRange(cols, box.Start, box.End, shift);
                int mid = (box.Start + box.End) / 2;
                boxes[target] = new Box { Start = box.Start, End = mid };
                boxes.Add(new Box { Start = mid, End = box.End });
            }

            var palette = new Color[boxes.Count];
            for (int i = 0; i < boxes.Count; i++) palette[i] = Average(cols, boxes[i]);
            return palette;
        }

        private static int ChannelRange(int[] cols, Box box, out int channel)
        {
            int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
            for (int i = box.Start; i < box.End; i++)
            {
                int c = cols[i];
                int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                if (b < bMin) bMin = b; if (b > bMax) bMax = b;
            }
            int rr = rMax - rMin, gr = gMax - gMin, br = bMax - bMin;
            if (rr >= gr && rr >= br) { channel = 2; return rr; } // channel: 0=B,1=G,2=R (shift = channel*8)
            if (gr >= br) { channel = 1; return gr; }
            channel = 0; return br;
        }

        private static void SortRange(int[] cols, int start, int end, int shift) =>
            Array.Sort(cols, start, end - start, Comparer<int>.Create((x, y) =>
                ((x >> shift) & 0xFF).CompareTo((y >> shift) & 0xFF)));

        private static Color Average(int[] cols, Box box)
        {
            long r = 0, g = 0, b = 0;
            int n = box.End - box.Start;
            if (n <= 0) return Color.Black;
            for (int i = box.Start; i < box.End; i++)
            {
                int c = cols[i];
                r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF;
            }
            return Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
        }

        private static byte[] MapToPalette(int[] pixels, Color[] palette)
        {
            // Cache nearest-index per distinct colour - screenshots repeat colours heavily.
            var cache = new Dictionary<int, byte>();
            var indices = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                int c = pixels[i];
                if (!cache.TryGetValue(c, out byte idx)) { idx = Nearest(c, palette); cache[c] = idx; }
                indices[i] = idx;
            }
            return indices;
        }

        private static byte Nearest(int c, Color[] palette)
        {
            int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
            int best = 0, bestDist = int.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                int dr = r - palette[i].R, dg = g - palette[i].G, db = b - palette[i].B;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist) { bestDist = dist; best = i; if (dist == 0) break; }
            }
            return (byte)best;
        }
    }
}
