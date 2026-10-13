using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using Hpgl.Rendering;
using Newtonsoft.Json.Linq;
using static GpibMcp.Tools.ToolArgs;

namespace GpibMcp.Tools
{
    /// <summary>
    /// <c>visa_query_binary</c> (#139): a query whose reply is bytes - a screen image, a waveform record -
    /// read as bytes, saved to a file, and described. An image is also returned as PNG content.
    /// </summary>
    public static class BinaryTools
    {
        /// <summary>An image returned as MCP content is downscaled to stay under this.</summary>
        public const long MaxImageContentBytes = 1000 * 1000;

        public const int DefaultTimeoutMs = 30000;

        public static void Register(ToolRegistry registry, IInstrumentManager visa)
        {
            registry.Add(new McpTool(
                "visa_query_binary",
                "Send a query whose reply is BINARY - an IEEE 488.2 block such as a screen image " +
                "(e.g. ':HCOP:SDUM:DATA? GIF', ':DISP:DATA?') or a waveform record - and read it as bytes, never " +
                "as text. The bytes are saved to a file (Pictures\\GpibMcp Captures unless save_path/save_dir say " +
                "otherwise); the result gives the path, byte count, type (from the first bytes: GIF, PNG, BMP, " +
                "JPEG, WMF, EMF or unknown), SHA-256 and first bytes. An image is also returned as a PNG you can " +
                "see, saved next to the original. Use this when visa_query says a reply is binary.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::17::INSTR'."),
                    Required("command", "string", "The query, e.g. ':HCOP:SDUM:DATA? GIF'."),
                    Prop("timeout_ms", "integer", "I/O timeout per read in ms (default 30000 - slow GPIB transfers)."),
                    Prop("block_format", "string", "'ieee488.2' (default): a #<n><length> block. 'raw': unframed bytes " +
                        "read until the instrument ends its message."),
                    Prop("save_path", "string", "Full path (with filename) to save the bytes to."),
                    Prop("save_dir", "string", "Folder to save into; a timestamped name with the resource is used.")),
                (Func<JObject, ToolOutput>)(args => Run(args, visa))));
        }

        private static ToolOutput Run(JObject args, IInstrumentManager visa)
        {
            string resource = ReqStr(args, "resource");
            string command = ReqStr(args, "command");
            int timeout = Int(args, "timeout_ms", DefaultTimeoutMs);
            string formatArg = Str(args, "block_format", "ieee488.2");
            BlockFormat format;
            if (string.Equals(formatArg, "raw", StringComparison.OrdinalIgnoreCase)) format = BlockFormat.Raw;
            else if (Regex.IsMatch(formatArg ?? "", @"^ieee[\s-]?488(\.2)?$", RegexOptions.IgnoreCase)) format = BlockFormat.Ieee4882;
            else return ToolOutput.Text("Unknown block_format '" + formatArg + "': use 'ieee488.2' or 'raw'.").AsError();

            BinaryBlockResult block = visa.QueryBinary(resource, command, timeout, format);
            byte[] bytes = block.Payload;
            string kind = Kind(bytes);

            string saved = CaptureTools.SaveFile(args, "binary-" + resource, Extension(kind), bytes);
            byte[] png = null;
            string pngSaved = null, imageNote = null;
            if (IsImage(kind))
            {
                try
                {
                    png = ScreenImage.ToPng(bytes, MaxImageContentBytes);
                    pngSaved = kind == "PNG" && png.Length == bytes.Length ? saved
                        : saved != null ? CaptureTools.SaveFile(new JObject { ["save_path"] = System.IO.Path.ChangeExtension(saved, ".png") }, null, ".png", png) : null;
                }
                catch (Exception ex) { imageNote = "it looks like " + kind + " but didn't decode as an image: " + ex.Message; }
            }

            string sha;
            using (var sha256 = SHA256.Create()) sha = string.Concat(sha256.ComputeHash(bytes).Select(x => x.ToString("x2")));
            string first = BinaryBlock.Hex(bytes.Take(16).ToArray());

            var structured = new JObject
            {
                ["resource"] = resource,
                ["command"] = command,
                ["bytes"] = bytes.Length,
                ["type"] = kind,
                ["sha256"] = sha,
                ["first_bytes_hex"] = first
            };
            if (block.Header != null) structured["header"] = block.Header;
            if (saved != null) structured["saved_to"] = saved;
            if (pngSaved != null) structured["png_saved_to"] = pngSaved;

            string text = resource + " " + command + " -> " + bytes.Length + " bytes, " + kind +
                          (block.Header != null ? " (block " + block.Header + ")" : " (raw)") + ".\n" +
                          "Saved to: " + (saved ?? "(could not be saved)") +
                          (pngSaved != null && pngSaved != saved ? "\nAs PNG: " + pngSaved : "") +
                          "\nSHA-256: " + sha + "\nFirst bytes: " + first +
                          (imageNote != null ? "\nNote: " + imageNote : "") +
                          (png != null ? "\nThe image is attached below as PNG." : "");

            var output = ToolOutput.Text(text).WithStructured(structured);
            if (png != null) output.AddImage(png, "image/png");
            return output;
        }

        /// <summary>The type of a binary reply, from its first bytes.</summary>
        public static string Kind(byte[] b)
        {
            if (b == null || b.Length < 4) return "unknown";
            if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return "GIF";
            if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "PNG";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG";
            if (b[0] == 'B' && b[1] == 'M') return "BMP";
            if (b[0] == 0xD7 && b[1] == 0xCD && b[2] == 0xC6 && b[3] == 0x9A) return "WMF";                   // placeable
            if ((b[0] == 1 || b[0] == 2) && b[1] == 0 && b[2] == 9 && b[3] == 0) return "WMF";                   // standard
            if (b.Length >= 44 && b[0] == 1 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
                b[40] == ' ' && b[41] == 'E' && b[42] == 'M' && b[43] == 'F') return "EMF";
            return "unknown";
        }

        public static bool IsImage(string kind) => kind != "unknown";

        public static string Extension(string kind)
        {
            switch (kind)
            {
                case "GIF": return ".gif";
                case "PNG": return ".png";
                case "JPEG": return ".jpg";
                case "BMP": return ".bmp";
                case "WMF": return ".wmf";
                case "EMF": return ".emf";
                default: return ".bin";
            }
        }

        /// <summary>
        /// Whether a text reply is really binary: an IEEE 488.2 block header ("#540200") followed by bytes
        /// that aren't text, or mostly-not-text. visa_query then points at visa_query_binary (#139).
        /// </summary>
        public static bool LooksBinary(string reply, out string header)
        {
            header = null;
            if (string.IsNullOrEmpty(reply)) return false;
            int control = reply.Count(c => (c < 0x20 && c != '\t' && c != '\r' && c != '\n') || (c >= 0x7F && c < 0xA0));
            var m = Regex.Match(reply, @"^\s*(#[1-9])(\d+)");
            if (m.Success)
            {
                int n = m.Groups[1].Value[1] - '0';
                if (m.Groups[2].Value.Length >= n) header = m.Groups[1].Value + m.Groups[2].Value.Substring(0, n);
                if (control > 0) return true;
            }
            return reply.Length >= 16 && control * 10 > reply.Length;
        }
    }
}
