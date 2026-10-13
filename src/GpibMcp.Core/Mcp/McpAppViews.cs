using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// The MCP Apps views this server serves (io.modelcontextprotocol/ui, #157). A tool whose descriptor names
    /// a view in <c>_meta.ui.resourceUri</c> is drawn by a host that supports MCP Apps - Claude Desktop - as
    /// that HTML page, inline under the tool call, fed the tool's result. Today there is one: the screen view,
    /// which shows an instrument's screen image to the user in full colour.
    /// </summary>
    public static class McpAppViews
    {
        /// <summary>The extension a client declares in its capabilities when it can draw views.</summary>
        public const string Extension = "io.modelcontextprotocol/ui";

        public const string MimeType = "text/html;profile=mcp-app";

        public const string ScreenViewUri = "ui://gpib-mcp/screen-view";

        /// <summary>The view-only tool the screen view fetches a saved image through.</summary>
        public const string ScreenImageTool = "screen_view_image";

        private const string ResourcePrefix = "GpibMcp.Mcp.Views.";
        private const string SdkResource = ResourcePrefix + "ext-apps-1.7.5-app-with-deps.js";
        private const string ScreenViewResource = ResourcePrefix + "screen-view.html";
        private const string SdkMarker = "/*@@MCP_APPS_SDK@@*/";

        private static readonly Lazy<string> ScreenViewHtml = new Lazy<string>(BuildScreenView);

        /// <summary>True when the client's capabilities say it can draw views.</summary>
        public static bool Declared(JObject capabilities)
        {
            var extensions = capabilities?["extensions"] as JObject;
            return extensions?[Extension] != null;
        }

        /// <summary>The extension's entry in this server's capabilities.</summary>
        public static JObject ServerCapability() => new JObject { ["mimeTypes"] = new JArray(MimeType) };

        /// <summary>resources/list.</summary>
        public static JArray List() => new JArray
        {
            new JObject
            {
                ["uri"] = ScreenViewUri,
                ["name"] = "screen-view",
                ["title"] = "Instrument screen",
                ["description"] = "Shows an instrument's screen image, as captured or read by a tool.",
                ["mimeType"] = MimeType
            }
        };

        /// <summary>resources/read: the view, or null for a URI that isn't one.</summary>
        public static JObject Read(string uri)
        {
            if (!string.Equals(uri, ScreenViewUri, StringComparison.Ordinal)) return null;
            return new JObject
            {
                ["contents"] = new JArray
                {
                    new JObject
                    {
                        ["uri"] = ScreenViewUri,
                        ["mimeType"] = MimeType,
                        ["text"] = ScreenViewHtml.Value,
                        // Everything is inline, so the default sandbox policy (inline script, data: images) fits.
                        ["_meta"] = new JObject { ["ui"] = new JObject { ["prefersBorder"] = true } }
                    }
                }
            };
        }

        /// <summary>
        /// The screen view with the MCP Apps client inlined. The client is an ES module ending in one
        /// <c>export{... as App ...}</c>; that is replaced with a global the view's own script picks up.
        /// </summary>
        private static string BuildScreenView()
        {
            string html = ReadResource(ScreenViewResource);
            return html.Replace(SdkMarker, InlineSdk(ReadResource(SdkResource)));
        }

        /// <summary>The client module rewritten to publish <c>App</c> as <c>window.__gpibMcpApp</c>.</summary>
        internal static string InlineSdk(string sdk)
        {
            Match export = Regex.Match(sdk, @"export\s*\{([^}]*)\}\s*;?\s*$");
            Match app = export.Success ? Regex.Match(export.Groups[1].Value, @"([\w$]+)\s+as\s+App\b") : Match.Empty;
            if (!app.Success)
                throw new InvalidOperationException("The MCP Apps client doesn't end with an export of App; see Views/vendor/README.md.");
            return sdk.Substring(0, export.Index) + "window.__gpibMcpApp=" + app.Groups[1].Value + ";";
        }

        private static string ReadResource(string name)
        {
            using (Stream s = typeof(McpAppViews).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("Missing embedded resource " + name);
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }
    }
}
