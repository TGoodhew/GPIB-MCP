using System;
using System.IO;
using GpibMcp.Mcp;
using GpibMcp.Printing;
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
                "their printers: this tool enumerates them for you. To then print, pass a capture's send-by-reference " +
                "handle as 'path' (typically a 'print' PCL capture: instrument_capture_screen format=\"print\") and " +
                "printer=<one of the listed names> (omit printer to use the default). The bytes are spooled RAW so the " +
                "printer's own page-language interpreter renders them - they go disk -> spooler, never back through you. " +
                "IMPORTANT: RAW printing needs the chosen printer to understand the bytes' page language - the instrument " +
                "emits older PCL, so a modern PCL-capable laser usually works, but a non-PCL / host-based (GDI-only) " +
                "printer will print blank or garbled (then internal render-then-print is needed, a separate path). To " +
                "send to a plotter/printer on the GPIB BUS instead, use visa_write_raw(path=) not this tool.",
                Schema(
                    Prop("path", "string", "Server-side file to spool - a capture handle returned by instrument_capture_screen " +
                        "(its 'raw … saved (send-by-reference handle)' path), typically a .pcl print capture. Its bytes are sent RAW."),
                    Prop("printer", "string", "Target Windows printer name (exactly as Windows lists it). Omit to use the system " +
                        "default printer. Pass list=true to enumerate the available names first."),
                    Prop("list", "boolean", "If true, return the installed Windows printers (and which is the default) WITHOUT printing."),
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
                    WindowsRawPrinter.SendRaw(printer, bytes, doc);

                    return "OK (spooled " + bytes.Length + " bytes RAW to Windows printer '" + printer + "'). If it prints blank " +
                           "or garbled, that queue likely doesn't understand the instrument's page language (PCL) - then internal " +
                           "render-then-print is needed instead.";
                })));
        }
    }
}
