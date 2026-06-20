// -----------------------------------------------------------------------------
// HpglViewer - a simple WinForms harness for the Hpgl.Rendering library.
//
// Reads an HP-GL/2 file (defaults to the bundled Test/test.plt 8563E capture),
// renders it via Hpgl.Rendering, and shows it in a window. A headless
// "--out <png>" mode renders to a file and exits (useful for smoke tests / CI).
//
// Hpgl.Rendering's plotter-emulation/render technique is derived from the HP7470A
// Plotter Emulator (7470.cpp) by John Miles, KE5FX - http://www.ke5fx.com/
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Windows.Forms;
using Hpgl.Rendering;

namespace HpglViewer
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string outPng = GetOption(args, "--out");
            string path = FirstNonOption(args) ?? LocateDefaultPlt();

            // Headless render-to-file mode (no window) - lets the harness be smoke-tested.
            if (outPng != null)
            {
                if (path == null || !File.Exists(path))
                {
                    Console.Error.WriteLine("HpglViewer: input .plt not found.");
                    return 2;
                }
                byte[] png = HpglRenderer.RenderToPng(File.ReadAllBytes(path));
                File.WriteAllBytes(outPng, png);
                Console.WriteLine("Rendered " + path + " -> " + outPng + " (" + png.Length + " bytes)");
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(path));
            return 0;
        }

        private static string GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static string FirstNonOption(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal)) { i++; continue; } // skip --opt value
                return args[i];
            }
            return null;
        }

        /// <summary>
        /// Finds the sample plot: next to the exe (deployed Content), else by walking up
        /// the directory tree for a Test\test.plt (running from the source tree).
        /// </summary>
        private static string LocateDefaultPlt()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string beside = Path.Combine(exeDir, "test.plt");
            if (File.Exists(beside)) return beside;

            var dir = new DirectoryInfo(exeDir);
            for (int up = 0; up < 8 && dir != null; up++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Test", "test.plt");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
