using System;
using System.IO;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using Hpgl.Rendering;
using Newtonsoft.Json.Linq;
using static GpibMcp.Tools.ToolArgs;

namespace GpibMcp.Tools
{
    /// <summary>
    /// The instrument screen-capture tool: drives a model's HP-GL plot (via the capture profile in
    /// the database), renders the result to a PNG, and returns it as an inline image.
    /// </summary>
    public static class CaptureTools
    {
        public static void Register(ToolRegistry registry, InstrumentDatabase db,
                                    AssignmentStore assignments, IInstrumentManager visa)
        {
            registry.Add(new McpTool(
                "instrument_capture_screen",
                "Capture the instrument's screen as an image. Uses HP-GL plotter emulation per the " +
                "model's capture profile (e.g. an HP 8563E spectrum analyzer), renders it, and returns " +
                "the screenshot inline. The model is taken from the resource's assignment unless given.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::18::INSTR'."),
                    Prop("model", "string", "Model/profile to use if the resource isn't assigned."),
                    Prop("width", "integer", "Output image width in pixels (default 1024)."),
                    Prop("height", "integer", "Output image height in pixels (default 768)."),
                    Prop("background", "string", "'black' (default) or 'white'."),
                    Prop("return_hpgl", "boolean", "Also return the raw HP-GL/2 source text (default false)."),
                    Prop("save_path", "string", "Optional path to also save the PNG to. Defaults to a captures folder; the saved path is reported so you can open the image even if it isn't shown inline."),
                    Prop("timeout_ms", "integer", "Overall capture backstop in ms (default 30000).")),
                (Func<Newtonsoft.Json.Linq.JObject, ToolOutput>)(args =>
                {
                    string resource = ReqStr(args, "resource");

                    string model = Str(args, "model", null);
                    if (string.IsNullOrEmpty(model)) model = assignments.Get(resource);
                    if (string.IsNullOrEmpty(model))
                        return Error("No model is known for " + resource +
                                     ". Assign one with assign_instrument, or pass model=.");

                    InstrumentDefinition def;
                    if (!db.TryGet(model, out def))
                        return Error("Unknown model '" + model + "'. See instrument_list_models.");

                    CaptureProfile profile = def.Capture;
                    if (profile == null ||
                        !string.Equals(profile.Method, "hpgl", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(profile.PlotCommand))
                        return Error("Model '" + def.Model + "' has no HP-GL capture profile.");

                    var captureOptions = new CaptureOptions { OverallTimeoutMs = Int(args, "timeout_ms", 30000) };

                    CaptureResult capture;
                    try
                    {
                        capture = visa.CaptureScreen(resource, profile.PreRoll, profile.PlotCommand, captureOptions);
                    }
                    catch (Exception ex)
                    {
                        return Error("Capture failed for " + resource + ": " + ex.Message);
                    }

                    if (!string.IsNullOrEmpty(profile.PostRoll))
                    {
                        try { visa.Write(resource, profile.PostRoll, VisaInstrumentManager.DefaultTimeoutMs); }
                        catch { /* best effort - the post-roll is cosmetic (e.g. resume continuous sweep) */ }
                    }

                    if (capture.ByteCount < new CaptureOptions().MinPlotBytes)
                        return Error("No complete plot captured from " + resource + " (" + capture.ByteCount +
                                     " bytes, " + capture.Completion + "). Check the address/model and that " +
                                     "the instrument supports plotting.");

                    var renderOptions = new HpglRenderOptions
                    {
                        Width = Int(args, "width", 1024),
                        Height = Int(args, "height", 768),
                        Background = string.Equals(Str(args, "background", "black"), "white",
                                                   StringComparison.OrdinalIgnoreCase)
                            ? HpglBackground.White : HpglBackground.Black
                    };

                    byte[] png;
                    try
                    {
                        png = HpglRenderer.RenderToPng(
                            Encoding.GetEncoding("ISO-8859-1").GetBytes(capture.Hpgl), renderOptions);
                    }
                    catch (Exception ex)
                    {
                        return Error("Captured " + capture.ByteCount + " bytes but rendering failed: " + ex.Message);
                    }

                    // Also save the PNG to a file - Claude Desktop does not reliably render
                    // tool-result images inline, so a path the user can open is the dependable path.
                    string savedTo = SaveCapture(args, def.Model, png);

                    string caption = def.Model + " screen - " + capture.ByteCount + " bytes HP-GL, " +
                                     capture.Completion + ", " + capture.ElapsedMs + " ms";
                    if (savedTo != null) caption += "  |  saved to: " + savedTo;

                    var output = ToolOutput.Image(png, "image/png", caption);
                    if (Bool(args, "return_hpgl", false)) output.AddText(capture.Hpgl);
                    return output;
                })));
        }

        private static ToolOutput Error(string message) => ToolOutput.Text(message).AsError();

        /// <summary>Saves the PNG to disk (best effort) and returns the path, or null on failure.</summary>
        private static string SaveCapture(JObject args, string model, byte[] png)
        {
            try
            {
                string path = Str(args, "save_path", null);
                if (string.IsNullOrEmpty(path))
                {
                    string dir = Environment.GetEnvironmentVariable("GPIB_MCP_CAPTURE_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "GpibMcp", "captures");
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, "capture-" + SanitizeFileName(model) + "-" +
                                             DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                }
                else
                {
                    string parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                }
                File.WriteAllBytes(path, png);
                return path;
            }
            catch (Exception)
            {
                return null; // saving is a convenience; never fail the capture over it
            }
        }

        private static string SanitizeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
