using System;
using System.IO;
using System.Text;
using GpibMcp.Instruments;
using Hpgl.Rendering;

namespace CaptureHarness
{
    /// <summary>
    /// Live-hardware harness for screen capture. Drives the REAL capture path against a REAL instrument
    /// over NI-VISA, with no Claude Desktop and no MCP/stdio layer - so a green run here means the
    /// server's instrument_capture_screen tool will behave identically.
    ///
    /// It reuses the production pieces exactly: <see cref="InstrumentManager.CaptureScreen"/> for I/O
    /// (plotter emulation for plot, printer stream for print), the bundled+user database for the
    /// capture profile, and <see cref="HpglRenderer"/>/<see cref="PclRenderer"/> for rendering.
    ///
    /// It writes TWO files: the raw capture bytes (a fixture for regression tests) and a rendered PNG.
    ///
    /// Usage:
    ///   CaptureHarness &lt;resource&gt; [--print|--plot] [--model M] [--out PATHBASE] [--timeout ms] [--white]
    ///
    /// Examples:
    ///   CaptureHarness GPIB0::18::INSTR --print --model 8563E
    ///   CaptureHarness GPIB0::18::INSTR --plot  --out C:\tmp\8563e
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (GpibOperationException gex) { Console.Error.WriteLine("GPIB/VISA error: " + gex.Detail); return 1; }
            catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help") { Usage(); return 1; }

            string resource = args[0];
            bool print = false;
            string model = null, outBase = null;
            int timeout = 30000;
            bool white = false;
            string preroll = null, postroll = null;
            bool prerollSet = false, postrollSet = false;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--print": print = true; break;
                    case "--plot": print = false; break;
                    case "--white": white = true; break;
                    case "--model": model = Next(args, ref i); break;
                    case "--out": outBase = Next(args, ref i); break;
                    case "--timeout": int.TryParse(Next(args, ref i), out timeout); break;
                    case "--preroll": preroll = Next(args, ref i) ?? ""; prerollSet = true; break;
                    case "--postroll": postroll = Next(args, ref i) ?? ""; postrollSet = true; break;
                    default: Console.Error.WriteLine("Unknown arg: " + args[i]); Usage(); return 1;
                }
            }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var db = InstrumentDatabase.Load(InstrumentPaths.DatabaseDirectories(exeDir));
            var assignments = AssignmentStore.FromFile(InstrumentPaths.BindingsPath());

            if (string.IsNullOrEmpty(model)) model = assignments.Get(resource);
            if (string.IsNullOrEmpty(model))
            {
                Console.Error.WriteLine("No model for " + resource + ". Pass --model <name>.");
                return 1;
            }

            InstrumentDefinition def;
            if (!db.TryGet(model, out def)) { Console.Error.WriteLine("Unknown model '" + model + "'."); return 1; }

            CaptureProfile profile = def.Capture;
            if (profile == null || string.IsNullOrEmpty(profile.PlotCommand))
            {
                Console.Error.WriteLine("Model '" + def.Model + "' has no capture profile.");
                return 1;
            }
            if (print && !profile.CanPrint)
            {
                Console.Error.WriteLine("Model '" + def.Model + "' has no printCommand - it can only plot.");
                return 1;
            }

            string command = print ? profile.PrintCommand : profile.PlotCommand;
            // preRoll/postRoll default to the profile's, but can be overridden (incl. "" to suppress) so
            // a plot and a print can be taken of the SAME frozen sweep: freeze on the plot, don't resume;
            // don't re-sweep on the print, then resume.
            string pre = prerollSet ? preroll : profile.PreRoll;
            string post = postrollSet ? postroll : profile.PostRoll;
            var options = new CaptureOptions
            {
                OverallTimeoutMs = timeout,
                Mode = print ? CaptureMode.PrinterStream : CaptureMode.PlotterEmulation
            };

            Console.WriteLine("=== capture harness - real instrument over NI-VISA ===");
            Console.WriteLine("  resource : " + resource);
            Console.WriteLine("  model    : " + model);
            Console.WriteLine("  format   : " + (print ? "print (PCL)" : "plot (HP-GL)"));
            Console.WriteLine("  preRoll  : " + (string.IsNullOrEmpty(pre) ? "(none)" : pre));
            Console.WriteLine("  command  : " + command);
            Console.WriteLine("  postRoll : " + (string.IsNullOrEmpty(post) ? "(none)" : post));
            Console.WriteLine();

            CaptureResult capture;
            using (var visa = new InstrumentManager(new NiVisaTransport()))
            {
                capture = visa.CaptureScreen(resource, pre, command, options);
                if (!string.IsNullOrEmpty(post))
                {
                    try { visa.Write(resource, post, InstrumentManager.DefaultTimeoutMs); }
                    catch (Exception ex) { Console.WriteLine("  (post-roll failed: " + ex.Message + ")"); }
                }
            }

            byte[] raw = Encoding.GetEncoding("ISO-8859-1").GetBytes(capture.Hpgl);
            Console.WriteLine("RESULT: " + capture.ByteCount + " bytes, " + capture.Completion +
                              ", " + capture.ElapsedMs + " ms");

            if (string.IsNullOrEmpty(outBase))
                outBase = Path.Combine(Environment.CurrentDirectory,
                    "capture-" + model + "-" + (print ? "print" : "plot"));

            string rawPath = outBase + (print ? ".pcl" : ".plt");
            string pngPath = outBase + ".png";
            File.WriteAllBytes(rawPath, raw);

            var renderOptions = new HpglRenderOptions
            {
                Background = white ? HpglBackground.White : HpglBackground.Black
            };
            byte[] png = print ? PclRenderer.RenderToPng(raw, renderOptions)
                               : HpglRenderer.RenderToPng(raw, renderOptions);
            File.WriteAllBytes(pngPath, png);

            Console.WriteLine("  raw  -> " + rawPath + " (" + raw.Length + " bytes)");
            Console.WriteLine("  png  -> " + pngPath + " (" + png.Length + " bytes)");
            if (print) Console.WriteLine("  looksLikePcl: " + PclRenderer.LooksLikePcl(raw));
            return capture.ByteCount > 0 ? 0 : 2;
        }

        private static string Next(string[] args, ref int i) => (++i < args.Length) ? args[i] : null;

        private static void Usage() => Console.Error.WriteLine(
            "Usage:\n" +
            "  CaptureHarness <resource> [--print|--plot] [--model M] [--out PATHBASE]\n" +
            "                 [--preroll \"CMDS\"] [--postroll \"CMDS\"] [--timeout ms] [--white]\n\n" +
            "  --preroll/--postroll override the profile's (pass \"\" to suppress) - e.g. capture a plot\n" +
            "  and a print of the SAME frozen sweep: plot --postroll \"\", then print --preroll \"\".\n\n" +
            "Examples:\n" +
            "  CaptureHarness GPIB0::18::INSTR --print --model 8563E\n" +
            "  CaptureHarness GPIB0::18::INSTR --plot  --out C:\\tmp\\8563e");
    }
}
