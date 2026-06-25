using System;
using System.IO;
using GpibMcp.Mcp;
using GpibMcp.Printing;
using Hpgl.Rendering;
using Newtonsoft.Json.Linq;
using static GpibMcp.Tools.ToolArgs;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Sends a captured instrument hardcopy to a Windows printer the PC already knows about (#83), by
    /// spooling the retained capture bytes RAW so the printer's own page-language interpreter renders them.
    /// Pairs with the #79 send-by-reference handle: the bytes go disk -&gt; spooler, never through the model.
    /// </summary>
    public static class PrintTools
    {
        public static void Register(ToolRegistry registry)
        {
            registry.Add(new McpTool(
                "print_capture_to_windows",
                "LIST the Windows printers this PC knows about, and/or PRINT a captured instrument hardcopy to one of " +
                "them. When the user asks to list/see/which Windows (or local/network) printers are available, CALL THIS " +
                "with list=true - it returns the installed printer names and the default directly. Do NOT tell the user " +
                "to run Get-Printer / Get-CimInstance / any PowerShell or external command, and do NOT say you can't see " +
                "their printers: this tool enumerates them for you. To print, pass a capture's send-by-reference handle as " +
                "'path' (a 'plot' or 'print' capture from instrument_capture_screen) and printer=<one of the listed names> " +
                "(omit printer to use the default). The bytes go disk -> printer, never back through you.\n" +
                "TWO modes via 'mode':\n" +
                "  - mode=\"render\" (DEFAULT, works on ANY printer): the server renders the capture itself (clean, " +
                "landscape, fit-to-page, our fonts) and prints it through the Windows driver. Use this by default - it " +
                "handles orientation/scaling and does not depend on the printer's page language.\n" +
                "  - mode=\"raw\": spool the captured bytes RAW so the printer's OWN interpreter renders them (native " +
                "fidelity). Only for a PCL 'print' capture sent to a PCL-capable printer; a non-PCL / host-based printer " +
                "will print blank or garbled - if raw output looks wrong, retry with mode=\"render\".\n" +
                "To send to a plotter/printer on the GPIB BUS instead, use visa_write_raw(path=) not this tool.",
                Schema(
                    Prop("path", "string", "Server-side file to print - a capture handle returned by instrument_capture_screen " +
                        "(its 'raw … saved (send-by-reference handle)' path); a .hpgl plot or .pcl print capture."),
                    Prop("printer", "string", "Target Windows printer name (exactly as Windows lists it). Omit to use the system " +
                        "default printer. Pass list=true to enumerate the available names first."),
                    Prop("list", "boolean", "If true, return the installed Windows printers (and which is the default) WITHOUT printing."),
                    Prop("mode", "string", "'render' (default) renders the capture and prints via the Windows driver - works on any " +
                        "printer; 'raw' spools the captured bytes verbatim for the printer's own interpreter (PCL-capable printers only)."),
                    Prop("orientation", "string", "'landscape' (default, suits instrument hardcopies) or 'portrait'. Applies to mode=render."),
                    Prop("doc_name", "string", "Spooler document name shown in the print queue (default 'GpibMcp <file>').")),
                (Func<JObject, string>)(args =>
                {
                    string path = Str(args, "path", null);
                    bool listOnly = Bool(args, "list", false) || string.IsNullOrWhiteSpace(path);

                    var printers = WindowsRawPrinter.InstalledPrinters();
                    string def = WindowsRawPrinter.DefaultPrinter();

                    if (listOnly)
                    {
                        if (printers.Count == 0)
                            return "No Windows printers are installed on this PC. (Add a printer in Windows, or use the GPIB " +
                                   "route visa_write_raw(path=) to send to a printer on the bus.)";
                        return "Installed Windows printers (default = " + (def ?? "none") + "):\n  - " +
                               string.Join("\n  - ", printers) +
                               "\n\nTo print a capture, call print_capture_to_windows(path=<capture handle>, printer=<one of the above>).";
                    }

                    path = path.Trim();
                    if (!File.Exists(path))
                        throw new ArgumentException("No file at path '" + path + "'. Pass a capture handle returned by " +
                                                    "instrument_capture_screen (a .pcl/.hpgl in %LOCALAPPDATA%\\GpibMcp\\captures).");

                    string mode = (Str(args, "mode", "render") ?? "render").Trim().ToLowerInvariant();
                    if (mode != "render" && mode != "raw")
                        throw new ArgumentException("Unknown mode '" + mode + "'. Use 'render' (default, any printer) or 'raw' (PCL-capable printers).");

                    string printer = Str(args, "printer", null);
                    if (string.IsNullOrWhiteSpace(printer)) printer = def;
                    if (string.IsNullOrWhiteSpace(printer))
                        throw new ArgumentException("No printer given and no Windows default printer is set. Pass printer=<name> " +
                                                    "(call with list=true to see the available names).");
                    if (!WindowsRawPrinter.IsInstalled(printer))
                        throw new ArgumentException("No Windows printer named '" + printer + "'. Installed: " +
                                                    (printers.Count == 0 ? "(none)" : string.Join(", ", printers)));

                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(path); }
                    catch (Exception ex) { throw new ArgumentException("Could not read '" + path + "': " + ex.Message); }

                    string doc = Str(args, "doc_name", null) ?? ("GpibMcp " + Path.GetFileName(path));

                    if (mode == "raw")
                    {
                        WindowsRawPrinter.SendRaw(printer, bytes, doc);
                        return "OK (spooled " + bytes.Length + " bytes RAW to Windows printer '" + printer + "'). If it prints blank " +
                               "or garbled, that queue doesn't understand the instrument's page language (PCL) - retry with mode=\"render\".";
                    }

                    // mode == "render" (default): render the capture ourselves and print via the Windows driver -
                    // works on any printer, clean orientation/scaling/fonts. A .pcl handle is a PCL print; otherwise HP-GL.
                    bool isPcl = path.EndsWith(".pcl", StringComparison.OrdinalIgnoreCase) || PclRenderer.LooksLikePcl(bytes);
                    bool landscape = !string.Equals(Str(args, "orientation", "landscape"), "portrait", StringComparison.OrdinalIgnoreCase);
                    WindowsRenderedPrinter.Print(bytes, isPcl, printer, landscape, doc);

                    return "OK (rendered the " + (isPcl ? "PCL print" : "HP-GL plot") + " capture and printed it to Windows printer '" +
                           printer + "', fit-to-page " + (landscape ? "landscape" : "portrait") + ").";
                })));
        }
    }
}
