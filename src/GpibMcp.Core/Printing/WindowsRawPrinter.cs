using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace GpibMcp.Printing
{
    /// <summary>
    /// Spools bytes to a Windows print queue as a RAW document (datatype <c>"RAW"</c>): the spooler passes
    /// them through untouched, so the printer's own page-language interpreter renders them. Used to send a
    /// captured instrument PCL hardcopy to a printer the PC already knows about (#83). The catch is that the
    /// target queue must understand the bytes' language (the instrument emits older PCL) - a non-PCL /
    /// host-based printer will not render a RAW PCL stream; for those, render-then-print is needed (#85).
    ///
    /// Windows-only (winspool.drv P/Invoke); the server already targets net472/Windows. Enumeration uses
    /// <see cref="PrinterSettings"/> (System.Drawing), the same framework the renderer relies on.
    /// </summary>
    public static class WindowsRawPrinter
    {
        /// <summary>The installed Windows printer queue names.</summary>
        public static IList<string> InstalledPrinters()
        {
            var list = new List<string>();
            foreach (string p in PrinterSettings.InstalledPrinters) list.Add(p);
            return list;
        }

        /// <summary>The system default printer name, or null if none/unavailable.</summary>
        public static string DefaultPrinter()
        {
            try
            {
                var name = new PrinterSettings().PrinterName;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }

        /// <summary>Whether a queue with this exact name (case-insensitive) is installed.</summary>
        public static bool IsInstalled(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (string p in PrinterSettings.InstalledPrinters)
                if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Spools <paramref name="bytes"/> to <paramref name="printerName"/> as one RAW document. Throws on
        /// any winspool failure; never silently drops bytes (verifies the written count).
        /// </summary>
        public static void SendRaw(string printerName, byte[] bytes, string docName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) throw new ArgumentException("printerName is required.");
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("no bytes to print.");

            IntPtr hPrinter;
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
                throw new InvalidOperationException("OpenPrinter('" + printerName + "') failed: " + LastError());
            try
            {
                var di = new DOC_INFO_1 { pDocName = docName ?? "GpibMcp capture", pDatatype = "RAW" };
                if (StartDocPrinter(hPrinter, 1, di) == 0)
                    throw new InvalidOperationException("StartDocPrinter failed: " + LastError());
                try
                {
                    if (!StartPagePrinter(hPrinter))
                        throw new InvalidOperationException("StartPagePrinter failed: " + LastError());

                    IntPtr unmanaged = Marshal.AllocHGlobal(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
                        int written;
                        if (!WritePrinter(hPrinter, unmanaged, bytes.Length, out written))
                            throw new InvalidOperationException("WritePrinter failed: " + LastError());
                        if (written != bytes.Length)
                            throw new InvalidOperationException("WritePrinter wrote " + written + " of " + bytes.Length + " bytes.");
                    }
                    finally { Marshal.FreeHGlobal(unmanaged); }

                    EndPagePrinter(hPrinter);
                }
                finally { EndDocPrinter(hPrinter); }
            }
            finally { ClosePrinter(hPrinter); }
        }

        private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class DOC_INFO_1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 di);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
    }
}
