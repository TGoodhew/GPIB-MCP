using System;
using System.Drawing;
using System.Drawing.Printing;
using System.Text;
using Hpgl.Rendering;

namespace GpibMcp.Printing
{
    /// <summary>
    /// Renders a captured hardcopy internally and prints it to a Windows queue through the normal GDI driver
    /// (<see cref="PrintDocument"/>), so it works on ANY installed printer regardless of page language - the
    /// driver scales and rasterises for its own device (#85). Unlike raw PCL spooling
    /// (<see cref="WindowsRawPrinter"/>), this fixes orientation/scaling/pagination and uses our own fonts;
    /// the trade-off is that the output is our rendering, not the instrument's native PCL. The capture is
    /// rendered on white paper (dark ink) and fit-to-page, preserving aspect ratio.
    /// </summary>
    public static class WindowsRenderedPrinter
    {
        // Render resolution for the intermediate bitmap. Landscape 4:3, high enough that fit-to-page on an
        // A4/Letter sheet stays crisp; the driver does the final scale to the device's DPI.
        private const int RenderWidth = 2200;
        private const int RenderHeight = 1650;

        /// <summary>
        /// Renders <paramref name="bytes"/> (a PCL print capture when <paramref name="isPcl"/>, else an HP-GL
        /// plot) to a white-paper bitmap and prints it fit-to-page on <paramref name="printerName"/>. Throws
        /// on any failure (e.g. an unavailable printer) - and validates the printer before spooling.
        /// </summary>
        public static void Print(byte[] bytes, bool isPcl, string printerName, bool landscape, string docName)
        {
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("no bytes to print.");
            if (string.IsNullOrWhiteSpace(printerName)) throw new ArgumentException("printerName is required.");

            var opts = new HpglRenderOptions
            {
                Background = HpglBackground.White,   // dark ink on white paper
                Width = RenderWidth,
                Height = RenderHeight,
                Antialias = true
            };

            Bitmap bmp = isPcl
                ? PclRenderer.RenderToBitmap(bytes, opts)
                : HpglRenderer.RenderToBitmap(DecodeLatin1(bytes), opts);
            try
            {
                using (var doc = new PrintDocument())
                {
                    doc.PrinterSettings.PrinterName = printerName;
                    if (!doc.PrinterSettings.IsValid)
                        throw new InvalidOperationException("Windows printer '" + printerName + "' is not available.");
                    doc.DocumentName = string.IsNullOrWhiteSpace(docName) ? "GpibMcp capture" : docName;
                    doc.DefaultPageSettings.Landscape = landscape;

                    doc.PrintPage += (s, e) =>
                    {
                        // Fit the bitmap into the printable area, preserving aspect ratio, centered.
                        Rectangle area = e.MarginBounds;
                        double scale = Math.Min((double)area.Width / bmp.Width, (double)area.Height / bmp.Height);
                        int w = Math.Max(1, (int)(bmp.Width * scale));
                        int h = Math.Max(1, (int)(bmp.Height * scale));
                        int x = area.Left + (area.Width - w) / 2;
                        int y = area.Top + (area.Height - h) / 2;
                        e.Graphics.DrawImage(bmp, new Rectangle(x, y, w, h));
                        e.HasMorePages = false;   // a hardcopy is always a single page
                    };

                    doc.Print();
                }
            }
            finally { bmp.Dispose(); }
        }

        private static string DecodeLatin1(byte[] bytes) =>
            Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
    }
}
