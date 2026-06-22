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
                "Capture the instrument's screen. Uses HP-GL plotter emulation per the model's capture " +
                "profile (e.g. an HP 8563E spectrum analyzer), renders it, and returns it so it can be " +
                "shown INLINE in the chat. The result includes an SVG you should display by creating an " +
                "image/svg+xml artifact (Claude Desktop does not render tool-result image blocks inline). " +
                "The PNG is also saved to the user's Pictures folder; pass save_dir to store it elsewhere. " +
                "The model is taken from the resource's assignment unless given.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::18::INSTR'."),
                    Prop("model", "string", "Model/profile to use if the resource isn't assigned."),
                    Prop("width", "integer", "Output image width in pixels (default 1024)."),
                    Prop("height", "integer", "Output image height in pixels (default 768)."),
                    Prop("background", "string", "'black' (default) or 'white'."),
                    Prop("return_hpgl", "boolean", "Also return the raw HP-GL/2 source text (default false)."),
                    Prop("inline_svg", "boolean", "Return an SVG to display inline as an artifact (default true). Set false to fall back to image-block + saved-file only."),
                    Prop("fidelity", "string", "Inline-SVG label fidelity: 'high' = exact HP single-stroke plotter font " +
                        "(faithful, larger/slower to display); 'low' = simple text labels (renders noticeably faster). " +
                        "If the user has stated a preference, pass it on EVERY capture. Omit only until they've chosen."),
                    Prop("save_dir", "string", "Folder to save the PNG into (e.g. 'C:\\\\Users\\\\me\\\\Pictures\\\\captures'). Defaults to the user's Pictures folder. Use this for 'capture the screen and store it in <folder>'."),
                    Prop("save_path", "string", "Full path (including filename) to save the PNG to. Overrides save_dir."),
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
                        try { visa.Write(resource, profile.PostRoll, InstrumentManager.DefaultTimeoutMs); }
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

                    byte[] hpglBytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(capture.Hpgl);
                    bool inlineSvg = Bool(args, "inline_svg", true);

                    // Inline-SVG label fidelity: 'low' = compact <text> labels (fast); 'high' = exact stroke
                    // font (faithful). Until the user has chosen, render high and prompt them to pick.
                    string fidelity = (Str(args, "fidelity", null) ?? "").Trim().ToLowerInvariant();
                    bool fidelityChosen = fidelity == "low" || fidelity == "high";
                    renderOptions.SvgTextLabels = fidelity == "low";

                    byte[] png;
                    string svg = null;
                    try
                    {
                        png = HpglRenderer.RenderToPng(hpglBytes, renderOptions); // PNG always uses the exact stroke font
                        if (inlineSvg) svg = HpglRenderer.RenderToSvg(hpglBytes, renderOptions);
                    }
                    catch (Exception ex)
                    {
                        return Error("Captured " + capture.ByteCount + " bytes but rendering failed: " + ex.Message);
                    }

                    // Always save the PNG so the user has a durable copy (default: their Pictures folder).
                    string savedTo = SaveCapture(args, def.Model, png);

                    string meta = def.Model + " screen - " + capture.ByteCount + " bytes HP-GL, " +
                                  capture.Completion + ", " + capture.ElapsedMs + " ms";
                    if (fidelityChosen) meta += "  |  " + fidelity + " fidelity";
                    if (savedTo != null) meta += "  |  saved to: " + savedTo;

                    var output = new ToolOutput();
                    if (inlineSvg && svg != null)
                    {
                        // Claude Desktop renders artifacts inline, not tool-result image blocks. Lead with an
                        // unmistakable directive: the user can ONLY see this via an artifact, and the image
                        // block below is visible to the model alone - so "seeing" it is not "showing" it.
                        // Tell the model to PASTE THE SVG VERBATIM (copying is fast/exact; redrawing stalls).
                        output.AddText(
                            "ACTION REQUIRED - show this capture to the user now. They CANNOT see it unless you " +
                            "create an artifact; the image block in this result is visible only to you, not in their " +
                            "chat, so describing or summarising the screen does NOT show it to them. Create an artifact " +
                            "of type `image/svg+xml` and paste the SVG below into it VERBATIM (copy it exactly - do not " +
                            "redraw, regenerate, simplify, or describe it). Do this before saying anything else.\n\n" +
                            meta + "\n\n" + svg);
                        output.AddImage(png, "image/png"); // also the raster, for vision / clients that render image blocks
                        if (!fidelityChosen) output.AddText(FidelityPrompt());
                    }
                    else
                    {
                        output.AddText(meta);
                        output.AddImage(png, "image/png");
                    }
                    if (Bool(args, "return_hpgl", false)) output.AddText(capture.Hpgl);
                    return output;
                })));
        }

        private static ToolOutput Error(string message) => ToolOutput.Text(message).AsError();

        /// <summary>Appended to a capture when the user hasn't chosen a fidelity yet: asks them to pick.</summary>
        private static string FidelityPrompt() =>
            "\n---\n" +
            "ASSISTANT: a screen-capture fidelity hasn't been chosen yet (this one used HIGH fidelity). After " +
            "showing the capture, ask the user which they'd like to use FROM NOW ON, and then remember it:\n" +
            "  - HIGH - the exact HP plotter stick-font: most faithful to the real 7475/7440/7550 look, but a " +
            "larger SVG so it takes a bit longer to render inline.\n" +
            "  - LOW  - simple text labels: renders noticeably faster (the trace and graticule are identical; only " +
            "the label font differs).\n" +
            "Once they choose, pass fidelity=\"low\" or fidelity=\"high\" on EVERY capture from then on (don't ask " +
            "again). Tell the user they can switch any time by saying e.g. \"use low-fidelity captures\" or " +
            "\"use high-fidelity captures\".";

        /// <summary>
        /// Saves the PNG to disk (best effort) and returns the path, or null on failure.
        /// Destination precedence: save_path (full file) &gt; save_dir (folder) &gt;
        /// GPIB_MCP_CAPTURE_DIR env &gt; the user's Pictures folder (\GpibMcp Captures).
        /// </summary>
        private static string SaveCapture(JObject args, string model, byte[] png)
        {
            try
            {
                string path = Str(args, "save_path", null);
                if (string.IsNullOrEmpty(path))
                {
                    string dir = Str(args, "save_dir", null);
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = Environment.GetEnvironmentVariable("GPIB_MCP_CAPTURE_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                            "GpibMcp Captures");
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
