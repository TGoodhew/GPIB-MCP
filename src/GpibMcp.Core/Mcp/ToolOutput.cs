using System;
using System.Collections.Generic;
using System.Text;

namespace GpibMcp.Mcp
{
    /// <summary>Kind of an MCP tool-result content block.</summary>
    public enum ToolContentKind
    {
        Text,
        Image
    }

    /// <summary>One MCP content block: a piece of text, or an image (base64 + MIME type).</summary>
    public sealed class ToolContentBlock
    {
        public ToolContentKind Kind { get; }
        public string Text { get; }
        public string Data { get; }       // base64-encoded bytes (image)
        public string MimeType { get; }

        private ToolContentBlock(ToolContentKind kind, string text, string data, string mimeType)
        {
            Kind = kind;
            Text = text;
            Data = data;
            MimeType = mimeType;
        }

        public static ToolContentBlock OfText(string text) =>
            new ToolContentBlock(ToolContentKind.Text, text ?? string.Empty, null, null);

        public static ToolContentBlock OfImage(byte[] bytes, string mimeType = "image/png") =>
            new ToolContentBlock(ToolContentKind.Image, null,
                Convert.ToBase64String(bytes ?? Array.Empty<byte>()), mimeType ?? "image/png");

        public static ToolContentBlock OfImageBase64(string base64, string mimeType = "image/png") =>
            new ToolContentBlock(ToolContentKind.Image, null, base64 ?? string.Empty, mimeType ?? "image/png");
    }

    /// <summary>
    /// A tool's result: an ordered list of content blocks (text and/or images) plus an error flag.
    /// Tools that just return a <see cref="string"/> produce a single text block; tools that produce
    /// an image (e.g. a captured instrument screen) add an image block that Claude renders inline.
    /// </summary>
    public sealed class ToolOutput
    {
        public List<ToolContentBlock> Content { get; } = new List<ToolContentBlock>();
        public bool IsError { get; set; }

        public static ToolOutput Text(string text)
        {
            var output = new ToolOutput();
            output.Content.Add(ToolContentBlock.OfText(text));
            return output;
        }

        /// <summary>An image result, optionally preceded by a caption/metadata text block.</summary>
        public static ToolOutput Image(byte[] png, string mimeType = "image/png", string caption = null)
        {
            var output = new ToolOutput();
            if (!string.IsNullOrEmpty(caption)) output.Content.Add(ToolContentBlock.OfText(caption));
            output.Content.Add(ToolContentBlock.OfImage(png, mimeType));
            return output;
        }

        public ToolOutput AddText(string text) { Content.Add(ToolContentBlock.OfText(text)); return this; }
        public ToolOutput AddImage(byte[] png, string mimeType = "image/png") { Content.Add(ToolContentBlock.OfImage(png, mimeType)); return this; }
        public ToolOutput AsError() { IsError = true; return this; }

        /// <summary>The concatenated text of all text blocks (convenience for callers/tests).</summary>
        public string AsText()
        {
            var sb = new StringBuilder();
            foreach (var block in Content)
            {
                if (block.Kind != ToolContentKind.Text) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(block.Text);
            }
            return sb.ToString();
        }
    }
}
