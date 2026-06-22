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
    /// The instrument screen-capture tool. Two hardcopy formats are supported, both rendered to an
    /// inline image: an HP-GL "plot" (vector plotter emulation - the default) and, where the model's
    /// capture profile provides a print command, a PCL "print" (raster hardcopy, issue #40).
    /// </summary>
    public static class CaptureTools
    {
        public static void Register(ToolRegistry registry, InstrumentDatabase db,
                                    AssignmentStore assignments, IInstrumentManager visa)
        {
            registry.Add(new McpTool(
                "instrument_capture_screen",
                "Capture the instrument's screen and return it so it can be shown INLINE in the chat. The " +
                "result includes an SVG you should display by creating an image/svg+xml artifact (Claude " +
                "Desktop does not render tool-result image blocks inline). The PNG is also saved to the " +
                "user's Pictures folder; pass save_dir to store it elsewhere. The model is taken from the " +
                "resource's assignment unless given.\n" +
                "FORMAT - 'plot' (HP-GL plotter emulation, vector) or 'print' (PCL raster hardcopy). Decide " +
                "as follows: if the user says SHOW the screen, use plot. If the user says CAPTURE the screen " +
                "(or is otherwise ambiguous) and the model supports printing, ASK them whether they want " +
                "plot or print before capturing. If the model only plots, just plot. Modern instruments " +
                "(e.g. Rigol scopes) instead return the screen as a direct image; those are captured " +
                "automatically and the format arg does not apply.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::18::INSTR'."),
                    Prop("model", "string", "Model/profile to use if the resource isn't assigned."),
                    Prop("format", "string", "'plot' (HP-GL vector, default) or 'print' (PCL raster hardcopy). " +
                        "Use 'plot' for 'show the screen'; ask the user plot-vs-print for 'capture the screen' " +
                        "when the model supports printing."),
                    Prop("width", "integer", "Output image width in pixels (default 1024)."),
                    Prop("height", "integer", "Output image height in pixels (default 768)."),
                    Prop("background", "string", "'black' (default) or 'white'."),
                    Prop("return_hpgl", "boolean", "Also return the raw HP-GL/2 (plot) or PCL (print) source (default false)."),
                    Prop("inline_svg", "boolean", "Return an SVG to display inline as an artifact (default true). Set false to fall back to image-block + saved-file only."),
                    Prop("fidelity", "string", "Plot only: inline-SVG label fidelity: 'high' = exact HP single-stroke plotter font " +
                        "(faithful, larger/slower to display); 'low' = simple text labels (renders noticeably faster). " +
                        "If the user has stated a preference, pass it on EVERY plot capture. Omit only until they've chosen."),
                    Prop("save_dir", "string", "Folder to save the PNG into (e.g. 'C:\\\\Users\\\\me\\\\Pictures\\\\captures'). Defaults to the user's Pictures folder. Use this for 'capture the screen and store it in <folder>'."),
                    Prop("save_path", "string", "Full path (including filename) to save the PNG to. Overrides save_dir."),
                    Prop("timeout_ms", "integer", "Overall capture backstop in ms (default 30000).")),
                (Func<JObject, ToolOutput>)(args => Capture(args, db, assignments, visa))));
        }

        private static ToolOutput Capture(JObject args, InstrumentDatabase db,
                                          AssignmentStore assignments, IInstrumentManager visa)
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
            if (profile == null)
                return Error("Model '" + def.Model + "' has no capture profile.");

            // SCPI boxes return the screen as a binary image block - a separate path, selected by the
            // profile's method, with no HP-GL/PCL rendering and no plot/print choice (issue #10).
            if (string.Equals(profile.Method, "scpi_block", StringComparison.OrdinalIgnoreCase))
                return CaptureScpiBlock(args, def, profile, visa);

            if (!string.Equals(profile.Method, "hpgl", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(profile.PlotCommand))
                return Error("Model '" + def.Model + "' has no HP-GL capture profile.");

            // Format selection: plot (HP-GL, default) or print (PCL). Print needs a profile print command.
            string format = (Str(args, "format", null) ?? "").Trim().ToLowerInvariant();
            bool formatChosen = format == "plot" || format == "print" || format == "pcl";
            bool isPrint = format == "print" || format == "pcl";
            if (isPrint && !profile.CanPrint)
                return Error("Model '" + def.Model + "' has no PCL print profile - it can only plot. " +
                             "Capture it with format=\"plot\".");

            string command = isPrint ? profile.PrintCommand : profile.PlotCommand;
            var captureOptions = new CaptureOptions
            {
                OverallTimeoutMs = Int(args, "timeout_ms", 30000),
                Mode = isPrint ? CaptureMode.PrinterStream : CaptureMode.PlotterEmulation
            };

            CaptureResult capture;
            try
            {
                capture = visa.CaptureScreen(resource, profile.PreRoll, command, captureOptions);
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

            string kind = isPrint ? "PCL" : "HP-GL";
            if (capture.ByteCount < new CaptureOptions().MinPlotBytes)
                return Error("No complete " + (isPrint ? "print" : "plot") + " captured from " + resource +
                             " (" + capture.ByteCount + " bytes, " + capture.Completion + "). Check the " +
                             "address/model and that the instrument supports " + (isPrint ? "printing" : "plotting") + ".");

            var renderOptions = new HpglRenderOptions
            {
                Width = Int(args, "width", 1024),
                Height = Int(args, "height", 768),
                Background = string.Equals(Str(args, "background", "black"), "white",
                                           StringComparison.OrdinalIgnoreCase)
                    ? HpglBackground.White : HpglBackground.Black
            };

            byte[] sourceBytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(capture.Hpgl);
            bool inlineSvg = Bool(args, "inline_svg", true);

            // Inline-SVG label fidelity (plot only): 'low' = compact <text> labels (fast); 'high' = exact
            // stroke font. PCL print output is raster, so fidelity does not apply to it.
            string fidelity = (Str(args, "fidelity", null) ?? "").Trim().ToLowerInvariant();
            bool fidelityChosen = fidelity == "low" || fidelity == "high";
            renderOptions.SvgTextLabels = fidelity == "low";

            byte[] png;
            string svg = null;
            try
            {
                if (isPrint)
                {
                    png = PclRenderer.RenderToPng(sourceBytes, renderOptions);
                    if (inlineSvg) svg = PclRenderer.RenderToSvg(sourceBytes, renderOptions);
                }
                else
                {
                    png = HpglRenderer.RenderToPng(sourceBytes, renderOptions); // PNG always uses the exact stroke font
                    if (inlineSvg) svg = HpglRenderer.RenderToSvg(sourceBytes, renderOptions);
                }
            }
            catch (Exception ex)
            {
                return Error("Captured " + capture.ByteCount + " bytes but rendering failed: " + ex.Message);
            }

            // Always save the PNG so the user has a durable copy (default: their Pictures folder).
            string savedTo = SaveCapture(args, def.Model + (isPrint ? "-print" : ""), png);

            string meta = def.Model + " screen - " + capture.ByteCount + " bytes " + kind + ", " +
                          capture.Completion + ", " + capture.ElapsedMs + " ms";
            if (isPrint) meta += "  |  print (PCL)";
            else if (fidelityChosen) meta += "  |  " + fidelity + " fidelity";
            if (savedTo != null) meta += "  |  saved to: " + savedTo;

            var output = new ToolOutput();
            if (inlineSvg && svg != null)
            {
                // Claude Desktop renders artifacts inline, not tool-result image blocks. Lead with an
                // unmistakable directive: the user can ONLY see this via an artifact, and the image
                // block below is visible to the model alone - so "seeing" it is not "showing" it.
                // Tell the model to PASTE THE SVG VERBATIM (copying is fast/exact; redrawing stalls).
                // Name the inline mechanism and explicitly rule out the file route - otherwise the model
                // can rationalise writing the SVG to a file, which is NOT shown inline (issue #48).
                output.AddText(
                    "ACTION REQUIRED - show this capture to the user now. They CANNOT see it unless you " +
                    "create an artifact; the image block in this result is visible only to you, not in their " +
                    "chat, so describing or summarising the screen does NOT show it to them. Create an artifact " +
                    "of type `image/svg+xml` and paste the SVG below into it VERBATIM (copy it exactly - do not " +
                    "redraw, regenerate, simplify, or describe it). Render it INLINE as an artifact only: do NOT " +
                    "write the SVG to a file, save it to outputs, or use any file-presentation path - a saved " +
                    "file is not shown inline in the chat and does NOT satisfy this. Do this before saying " +
                    "anything else.\n\n" +
                    meta + "\n\n" + svg);
                output.AddImage(png, "image/png"); // also the raster, for vision / clients that render image blocks
                if (!isPrint && !fidelityChosen) output.AddText(FidelityPrompt());
                if (!formatChosen && profile.CanPrint) output.AddText(FormatPrompt());
            }
            else
            {
                output.AddText(meta);
                output.AddImage(png, "image/png");
            }
            if (Bool(args, "return_hpgl", false)) output.AddText(capture.Hpgl);
            return output;
        }

        /// <summary>
        /// Inline-SVG byte budget for a SCPI screenshot. The model must paste the SVG verbatim, and a
        /// data-URI PNG is base64 - far harder to reproduce than a vector plot SVG. The working PCL print
        /// SVG is ~2.8 KB; an ~11 KB base64 blob stalls the model. So keep this near the proven-safe zone:
        /// a small preview shows, anything bigger falls back to the saved file (issue #10).
        /// </summary>
        private const int InlineSvgBudgetChars = 3400;

        /// <summary>
        /// SCPI screen dump (issue #10): query the instrument's image block (<c>:DISP:DATA?</c> et al.),
        /// strip the IEEE 488.2 header, and return the screenshot - saved full-res, shown inline as a
        /// bounded downscaled thumbnail (a full-colour screenshot can't be pasted verbatim at full size).
        /// </summary>
        private static ToolOutput CaptureScpiBlock(JObject args, InstrumentDefinition def,
                                                   CaptureProfile profile, IInstrumentManager visa)
        {
            if (string.IsNullOrEmpty(profile.DumpCommand))
                return Error("Model '" + def.Model + "' has a scpi_block profile but no dumpCommand.");

            string resource = ReqStr(args, "resource");
            int timeout = Int(args, "timeout_ms", 30000);

            if (!string.IsNullOrEmpty(profile.PreRoll))
            {
                try { visa.Write(resource, profile.PreRoll, InstrumentManager.DefaultTimeoutMs); }
                catch { /* best effort */ }
            }

            byte[] block;
            try { block = visa.QueryBlock(resource, profile.DumpCommand, timeout); }
            catch (Exception ex) { return Error("Screen dump failed for " + resource + ": " + ex.Message); }

            if (!string.IsNullOrEmpty(profile.PostRoll))
            {
                try { visa.Write(resource, profile.PostRoll, InstrumentManager.DefaultTimeoutMs); }
                catch { /* best effort */ }
            }

            byte[] imageBytes = Ieee4882Block.ExtractDefiniteLength(block);
            if (imageBytes == null || imageBytes.Length < 64)
                return Error("No image returned from " + resource + " (" + (imageBytes?.Length ?? 0) +
                             " bytes). Check the dump command and that the model returns a screen image.");

            byte[] png;
            int w, h;
            try
            {
                png = ScreenImage.ToPng(imageBytes);
                ScreenImage.Dimensions(imageBytes, out w, out h);
            }
            catch (Exception ex)
            {
                return Error("Captured " + imageBytes.Length + " bytes but it is not a decodable image: " + ex.Message);
            }

            string savedTo = SaveCapture(args, def.Model + "-screen", png);
            string savedWhere = savedTo != null ? "at " + savedTo : "in the user's Pictures folder";

            bool inlineSvg = Bool(args, "inline_svg", true);
            string svg = null;
            if (inlineSvg)
            {
                try { svg = ScreenImage.ToBoundedInlineSvg(imageBytes, InlineSvgBudgetChars); }
                catch { svg = null; }
            }

            string meta = def.Model + " screen - " + imageBytes.Length + " bytes image (" + w + "x" + h +
                          "), SCPI dump";
            if (savedTo != null) meta += "  |  saved to: " + savedTo;

            var output = new ToolOutput();
            if (svg != null)
            {
                // Same inline-artifact mechanism as a plot/print, but the embedded image is a DOWNSCALED
                // preview - so explicitly point the user at the full-resolution file (per Tony's request).
                output.AddText(
                    "ACTION REQUIRED - show this capture to the user now. They CANNOT see it unless you create " +
                    "an artifact; the image block in this result is visible only to you, not in their chat. Create " +
                    "an artifact of type `image/svg+xml` and paste the SVG below into it VERBATIM (copy it exactly - " +
                    "do not redraw, regenerate, simplify, or describe it). Render it INLINE as an artifact only: do " +
                    "NOT write the SVG to a file. IMPORTANT: the inline image is a DOWNSCALED preview - after showing " +
                    "it, tell the user the FULL-RESOLUTION screenshot is saved " + savedWhere + ".\n\n" +
                    meta + "\n\n" + svg);
                output.AddImage(png, "image/png"); // full-res raster for vision / clients that render image blocks
            }
            else
            {
                // Inline disabled, or a full-colour screenshot too detailed to paste verbatim as an
                // artifact without stalling: deliver via the saved file + the model-visible image block.
                output.AddText(
                    "This is a full-colour instrument screenshot - too detailed to render inline as an " +
                    "artifact (a large base64 image stalls the paste). It is saved at FULL RESOLUTION " +
                    savedWhere + ", and the image is attached below as a block that YOU can see. Do this: " +
                    "(1) describe what is on the instrument's screen for the user, and (2) tell them the " +
                    "full-resolution screenshot is saved " + savedWhere + ". Do NOT try to create an " +
                    "image/svg+xml artifact from it.\n\n" + meta);
                output.AddImage(png, "image/png");
            }
            return output;
        }

        private static ToolOutput Error(string message) => ToolOutput.Text(message).AsError();

        /// <summary>Appended to a plot capture when the user hasn't chosen a fidelity yet: asks them to pick.</summary>
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
        /// Appended when a capture format wasn't explicitly chosen and this model CAN print: nudges the
        /// assistant to confirm plot-vs-print when the user's intent was to "capture" (rather than "show").
        /// </summary>
        private static string FormatPrompt() =>
            "\n---\n" +
            "ASSISTANT: this model supports two capture formats and none was specified, so a PLOT was made. " +
            "If the user asked to SHOW the screen, plot is correct - no need to ask. If they asked to CAPTURE " +
            "the screen (or were ambiguous), ask which they want before the next capture:\n" +
            "  - PLOT  - HP-GL plotter emulation (vector): the screen drawn as the instrument would plot it.\n" +
            "  - PRINT - PCL raster hardcopy: the screen as the instrument would print it to an HP printer.\n" +
            "Then pass format=\"plot\" or format=\"print\" on the capture.";

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
