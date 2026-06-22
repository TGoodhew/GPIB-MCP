// -----------------------------------------------------------------------------
// Tests for the HP PCL raster decoder/renderer (issue #40).
//
// The fixtures are built directly from the worked examples in HP's "PCL 5 Printer
// Language Technical Reference Manual", Chapter 15 (Raster Graphics) - the same
// byte streams the manual shows for each compression method, so a passing test
// asserts spec conformance rather than self-consistency.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Hpgl.Rendering;
using Xunit;

namespace Hpgl.Rendering.Tests
{
    public class PclRendererTests
    {
        private const byte Esc = 0x1B;

        /// <summary>Fluent builder for a PCL byte stream.</summary>
        private sealed class Pcl
        {
            private readonly List<byte> _b = new List<byte>();
            public Pcl Esc(string seq) { _b.Add(0x1B); foreach (char c in seq) _b.Add((byte)c); return this; }
            public Pcl Raw(params byte[] bytes) { _b.AddRange(bytes); return this; }
            public Pcl Ascii(string s) { foreach (char c in s) _b.Add((byte)c); return this; }
            public byte[] ToArray() => _b.ToArray();
        }

        // Table 15-6 row: U U U U A T T  ->  0x55 x4, 0x41, 0x54 x2.
        private static readonly byte[] ExpectedRow156 = { 0x55, 0x55, 0x55, 0x55, 0x41, 0x54, 0x54 };

        [Fact]
        public void Decode_Unencoded_Method0_RoundTripsRow()
        {
            var pcl = new Pcl().Esc("*r1A").Esc("*b0m7W").Ascii("UUUUATT").Esc("*rC").ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Single(img.Rows);
            Assert.Equal(ExpectedRow156, img.Rows[0]);
            Assert.Equal(56, img.Width); // 7 bytes * 8
            Assert.Equal(1, img.Height);
        }

        [Fact]
        public void Decode_RunLength_Method1_MatchesUnencoded()
        {
            // ESC*b1m6W (3)U(0)A(1)T  -> 4xU, 1xA, 2xT
            var pcl = new Pcl().Esc("*r1A").Esc("*b1m6W")
                               .Raw(3, (byte)'U', 0, (byte)'A', 1, (byte)'T').Esc("*rC").ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Single(img.Rows);
            Assert.Equal(ExpectedRow156, img.Rows[0]);
        }

        [Fact]
        public void Decode_Tiff_Method2_MatchesUnencoded()
        {
            // ESC*b2m6W (-3)U(0)A(-1)T  -> repeat U x4, literal A, repeat T x2.  -3=253, -1=255.
            var pcl = new Pcl().Esc("*r1A").Esc("*b2m6W")
                               .Raw(253, (byte)'U', 0, (byte)'A', 255, (byte)'T').Esc("*rC").ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Single(img.Rows);
            Assert.Equal(ExpectedRow156, img.Rows[0]);
        }

        [Fact]
        public void Decode_Tiff_NopControlByte_IsSkipped()
        {
            // A -128 (0x80) NOP control byte must be ignored, and the next byte taken as the control byte.
            // Payload (7 bytes): NOP, (-3)U, (0)A, (-1)T  ->  U U U U A T T.
            var pcl = new Pcl().Esc("*r1A").Esc("*b2m7W")
                               .Raw(0x80, 253, (byte)'U', 0, (byte)'A', 255, (byte)'T').Esc("*rC").ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Single(img.Rows);
            Assert.Equal(ExpectedRow156, img.Rows[0]);
        }

        [Fact]
        public void Decode_DeltaRow_Method3_ThreeRowExample()
        {
            // Table 15-8: three rows built by delta from a zeroed seed.
            var pcl = new Pcl()
                .Esc("*r1A")                                   // start raster: seed = zeros
                .Esc("*b3m2W").Raw(0x01, 0xFF)                 // Row 1: replace 1 byte at offset 1
                .Esc("*b2W").Raw(0x02, 0xF0)                   // Row 2: replace 1 byte at offset 2
                .Esc("*b5W").Raw(0x00, 0x0F, 0x22, 0xAA, 0xAA) // Row 3: 1 byte @0, then 2 bytes @offset 2
                .Esc("*rC")
                .ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Equal(3, img.Rows.Count);
            Assert.Equal(new byte[] { 0x00, 0xFF, 0x00, 0x00, 0x00 }, img.Rows[0]);
            Assert.Equal(new byte[] { 0x00, 0xFF, 0xF0, 0x00, 0x00 }, img.Rows[1]);
            Assert.Equal(new byte[] { 0x0F, 0xFF, 0xF0, 0xAA, 0xAA }, img.Rows[2]);
        }

        [Fact]
        public void Decode_DeltaRow_ExtendedOffset_SumsTrailingBytes()
        {
            // Offset 31 in the command byte extends via following bytes until one < 255: 31+255+128 = 414.
            // command byte = (replace-1)<<5 | 31 = 0x1F (replace 1, offset marker 31).
            var pcl = new Pcl()
                .Esc("*r1A")
                .Esc("*b3m4W").Raw(0x1F, 255, 128, 0x99)  // delta: place 0x99 at byte 414
                .Esc("*rC")
                .ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Single(img.Rows);
            Assert.Equal(0x99, img.Rows[0][414]);
            Assert.Equal(0x00, img.Rows[0][413]);
        }

        [Fact]
        public void Decode_Adaptive_Method5_EmptyAndDuplicateRows()
        {
            // One unencoded row, then a duplicate of it, then one empty row.
            var block = new byte[] { 0, 0, 2, 0xFF, 0x00,   // op0 unencoded, 2 bytes: FF 00
                                     5, 0, 1,                // op5 duplicate x1
                                     4, 0, 1 };              // op4 empty x1
            var pcl = new Pcl().Esc("*r1A").Esc("*b5m" + block.Length + "W").Raw(block).Esc("*rC").ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Equal(3, img.Rows.Count);
            Assert.Equal(new byte[] { 0xFF, 0x00 }, img.Rows[0]);
            Assert.Equal(new byte[] { 0xFF, 0x00 }, img.Rows[1]);
            Assert.Equal(new byte[] { 0x00, 0x00 }, img.Rows[2]);
        }

        [Fact]
        public void Decode_RepeatRow_ZeroLengthTransfer_DuplicatesPreviousRow()
        {
            // Under delta compression, ESC*b0W repeats the previous raster row.
            var pcl = new Pcl()
                .Esc("*r1A").Esc("*b3m2W").Raw(0x01, 0xFF)  // row 1
                .Esc("*b0W")                                 // repeat row 1
                .Esc("*rC").ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Equal(2, img.Rows.Count);
            Assert.Equal(img.Rows[0], img.Rows[1]);
        }

        [Fact]
        public void Decode_RasterWidth_SetsImageWidthAndPadsRows()
        {
            var pcl = new Pcl().Esc("*r1A").Esc("*r80S")     // width = 80 px -> 10 bytes/row
                               .Esc("*b0m7W").Ascii("UUUUATT").Esc("*rC").ToArray();

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Equal(80, img.Width);
            Assert.Equal(10, img.RowBytes);
            Assert.Equal(10, img.Rows[0].Length);     // zero-padded to the declared width
        }

        [Fact]
        public void RenderToBitmap_HonorsSizeAndPaintsInk()
        {
            // A solid block of set bits should paint many ink pixels on a black canvas.
            var rowData = Enumerable.Repeat((byte)0xFF, 8).ToArray();
            var pcl = new Pcl().Esc("*t100R").Esc("*r1A").Esc("*r64S");
            for (int y = 0; y < 32; y++) pcl.Esc("*b0m8W").Raw(rowData);
            var bytes = pcl.Esc("*rC").ToArray();

            using (var bmp = PclRenderer.RenderToBitmap(bytes,
                       new HpglRenderOptions { Width = 320, Height = 240, Background = HpglBackground.Black }))
            {
                Assert.Equal(320, bmp.Width);
                Assert.Equal(240, bmp.Height);
                int ink = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y).ToArgb() != Color.Black.ToArgb()) ink++;
                Assert.True(ink > 1000, "expected the filled raster to paint many ink pixels, got " + ink);
            }
        }

        [Fact]
        public void RenderToPng_ProducesPngHeader()
        {
            var pcl = new Pcl().Esc("*r1A").Esc("*b0m2W").Raw(0xFF, 0xFF).Esc("*rC").ToArray();
            byte[] png = PclRenderer.RenderToPng(pcl);
            Assert.True(png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47);
        }

        [Fact]
        public void RenderToSvg_EmbedsPngDataUri()
        {
            var pcl = new Pcl().Esc("*r1A").Esc("*b0m2W").Raw(0xFF, 0xFF).Esc("*rC").ToArray();
            string svg = PclRenderer.RenderToSvg(pcl);
            Assert.Contains("<svg", svg);
            Assert.Contains("data:image/png;base64,", svg);
        }

        [Fact]
        public void LooksLikePcl_TrueForPcl_FalseForPlainHpgl()
        {
            var pcl = new Pcl().Esc("E").Esc("*r1A").Esc("*b0m1W").Raw(0xFF).ToArray();
            Assert.True(PclRenderer.LooksLikePcl(pcl));

            byte[] hpgl = Encoding.ASCII.GetBytes("IN;SP1;PU0,0;PD1000,1000;SP0;");
            Assert.False(PclRenderer.LooksLikePcl(hpgl));
        }

        // ---- real HP 8563E print dump (issue #40 bench fixture) -------------

        private static string FixturePath(string name) =>
            Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        [Fact]
        public void Decode_Real8563EPrint_IsExpectedRasterShape()
        {
            // A genuine 8563E PRINT 0; dump over GPIB: ESC=, ESC*rA, 338x (ESC*b80W + 80 unencoded
            // bytes), terminated by ESC*rB. No resolution/width/height/compression commands - the
            // simplest PCL - so the decoder must infer 640x338 from the row geometry.
            byte[] pcl = File.ReadAllBytes(FixturePath("test-print.pcl"));

            var img = PclRasterDecoder.Decode(pcl);

            Assert.Equal(640, img.Width);
            Assert.Equal(338, img.Height);
            Assert.Equal(80, img.RowBytes);
            Assert.All(img.Rows, r => Assert.Equal(80, r.Length));
            Assert.Null(img.EmbeddedHpgl);
        }

        [Fact]
        public void RenderToSvg_Real8563EPrint_StaysCompactForInlinePaste()
        {
            // The inline path asks the model to reproduce the SVG verbatim into an artifact. Embedding the
            // upscaled 1024x768 raster made a ~32 KB base64 blob that stalled the model (Desktop "hang");
            // the native 1-bit raster keeps it a few KB - on par with a plotted SVG.
            byte[] pcl = File.ReadAllBytes(FixturePath("test-print.pcl"));
            string svg = PclRenderer.RenderToSvg(pcl);
            Assert.Contains("data:image/png;base64,", svg);
            Assert.True(svg.Length < 12000, "print SVG must stay compact for inline paste; was " + svg.Length);
        }

        [Fact]
        public void Render_Real8563EPrint_ProducesNonBlankScreen()
        {
            byte[] pcl = File.ReadAllBytes(FixturePath("test-print.pcl"));

            using (var bmp = PclRenderer.RenderToBitmap(pcl,
                       new HpglRenderOptions { Width = 1024, Height = 768, Background = HpglBackground.Black }))
            {
                Assert.Equal(1024, bmp.Width);
                Assert.Equal(768, bmp.Height);
                int ink = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y).ToArgb() != Color.Black.ToArgb()) ink++;
                // The graticule + trace + annotation mark a substantial, but not overwhelming, area.
                Assert.True(ink > 20000, "expected the analyzer screen to be drawn, got " + ink + " px");
                Assert.True(ink < (long)bmp.Width * bmp.Height / 2, "expected mostly-black screen, got " + ink + " px");
            }
        }

        [Fact]
        public void Decode_EmbeddedHpgl_IsCapturedAndRoutedToVectorRenderer()
        {
            // A PCL job that enters HP-GL/2 mode and draws, with no raster of its own.
            const string vectors = "IN;SP1;PU0,0;PD3000,2000;PD0,2000;PU0,0;SP0;";
            var pcl = new Pcl().Esc("E").Esc("%0B").Ascii(vectors).Esc("%0A").Esc("E").ToArray();

            var img = PclRasterDecoder.Decode(pcl);
            Assert.True(img.IsEmpty);
            Assert.NotNull(img.EmbeddedHpgl);
            Assert.Contains("PD3000,2000", img.EmbeddedHpgl);

            // The renderer should hand the embedded vector block to HpglRenderer and draw it.
            using (var bmp = PclRenderer.RenderToBitmap(pcl,
                       new HpglRenderOptions { Width = 200, Height = 150, Background = HpglBackground.Black }))
            {
                int ink = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y).ToArgb() != Color.Black.ToArgb()) ink++;
                Assert.True(ink > 50, "expected the embedded HP-GL/2 vectors to be drawn, got " + ink);
            }
        }
    }
}
