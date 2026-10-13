using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GpibMcp.Mcp;
using Hpgl.Rendering;
using Newtonsoft.Json.Linq;
using static GpibMcp.Tools.ToolArgs;

namespace GpibMcp.Tools
{
    /// <summary>
    /// <c>screen_view_image</c> (#157): the tool the screen view calls to fetch a saved screen image, when the
    /// image didn't arrive in the tool result or the user saves the full-size PNG. Only a view may call it - it
    /// is hidden from the model and from clients without views - and it only serves images this server saved,
    /// or that sit in the capture folder: it is not a way to read files.
    /// </summary>
    public static class ScreenViewTools
    {
        private static readonly string[] ImageExtensions = { ".png", ".gif", ".bmp", ".jpg", ".jpeg", ".wmf", ".emf" };

        private const int RememberLast = 500;
        private static readonly object Gate = new object();
        private static readonly LinkedList<string> Saved = new LinkedList<string>();

        public static void Register(ToolRegistry registry)
        {
            registry.Add(new McpTool(
                McpAppViews.ScreenImageTool,
                "For the screen view only: returns a screen image this server saved, as base64 PNG.",
                Schema(Required("path", "string", "The saved image, as png_saved_to gave it.")),
                (Func<JObject, ToolOutput>)Run).ForViewsOnly());
        }

        /// <summary>Records a file this server saved, so the view may fetch it. Called by every capture save.</summary>
        public static void Remember(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full = Path.GetFullPath(path);
            lock (Gate)
            {
                Saved.Remove(full);
                Saved.AddFirst(full);
                while (Saved.Count > RememberLast) Saved.RemoveLast();
            }
        }

        /// <summary>
        /// Whether the view may have <paramref name="path"/>: an image this server process saved, or one in the
        /// capture folder (Claude Desktop runs more than one server process, and the view's call may reach another).
        /// </summary>
        public static bool MayServe(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return false; }
            if (!ImageExtensions.Contains(Path.GetExtension(full).ToLowerInvariant())) return false;
            lock (Gate)
                if (Saved.Contains(full, StringComparer.OrdinalIgnoreCase)) return true;
            string folder = CaptureTools.DefaultCaptureFolder();
            return string.Equals(Path.GetDirectoryName(full)?.TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase);
        }

        private static ToolOutput Run(JObject args)
        {
            string path = ReqStr(args, "path");
            if (!MayServe(path) || !File.Exists(path))
                return ToolOutput.Text("Not a screen image this server saved: " + path).AsError();

            byte[] bytes = File.ReadAllBytes(path);
            byte[] png = BinaryTools.Kind(bytes) == "PNG" ? bytes : ScreenImage.ToPng(bytes);
            ScreenImage.Dimensions(png, out int w, out int h);
            return ToolOutput.Text("PNG, " + png.Length + " bytes, " + w + "x" + h + ".")
                .WithStructured(new JObject
                {
                    ["mimeType"] = "image/png",
                    ["data"] = Convert.ToBase64String(png),
                    ["width"] = w,
                    ["height"] = h
                });
        }
    }
}
