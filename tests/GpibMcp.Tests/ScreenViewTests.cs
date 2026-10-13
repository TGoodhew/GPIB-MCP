using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// #157: the screen view - an MCP Apps view that Claude Desktop draws under a capture, showing the user the
    /// screen in full colour - and what changes for a client that draws views (and what doesn't for one that
    /// can't).
    /// </summary>
    [Collection("CaptureFiles")]
    public class ScreenViewTests
    {
        private static readonly string CaptureDir = Path.Combine(Path.GetTempPath(), "gpibmcp_captures_test");

        static ScreenViewTests()
        {
            // The same folders CaptureToolsTests uses, so default-path saves stay out of the user's Pictures.
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURE_DIR", CaptureDir);
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURE_TIMING_LOG", Path.Combine(CaptureDir, "capture-timing.log"));
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURES_DIR", Path.Combine(CaptureDir, "handles"));
        }

        /// <summary>A real 24x16 GIF89a, as the E4406A sends its screen.</summary>
        private static readonly byte[] Gif = Convert.FromBase64String(
            "R0lGODlhGAAQAPcAAAAAAAAAMwAAZgAAmQAAzAAA/wArAAArMwArZgArmQArzAAr/wBVAABVMwBVZgBVmQBVzABV/wCAAACAMwCAZgCAmQCAzACA/wCqAACqMwCqZgCqmQCqzACq/wDVAADVMwDVZgDVmQDVzADV/wD/AAD/MwD/ZgD/mQD/zAD//zMAADMAMzMAZjMAmTMAzDMA/zMrADMrMzMrZjMrmTMrzDMr/zNVADNVMzNVZjNVmTNVzDNV/zOAADOAMzOAZjOAmTOAzDOA/zOqADOqMzOqZjOqmTOqzDOq/zPVADPVMzPVZjPVmTPVzDPV/zP/ADP/MzP/ZjP/mTP/zDP//2YAAGYAM2YAZmYAmWYAzGYA/2YrAGYrM2YrZmYrmWYrzGYr/2ZVAGZVM2ZVZmZVmWZVzGZV/2aAAGaAM2aAZmaAmWaAzGaA/2aqAGaqM2aqZmaqmWaqzGaq/2bVAGbVM2bVZmbVmWbVzGbV/2b/AGb/M2b/Zmb/mWb/zGb//5kAAJkAM5kAZpkAmZkAzJkA/5krAJkrM5krZpkrmZkrzJkr/5lVAJlVM5lVZplVmZlVzJlV/5mAAJmAM5mAZpmAmZmAzJmA/5mqAJmqM5mqZpmqmZmqzJmq/5nVAJnVM5nVZpnVmZnVzJnV/5n/AJn/M5n/Zpn/mZn/zJn//8wAAMwAM8wAZswAmcwAzMwA/8wrAMwrM8wrZswrmcwrzMwr/8xVAMxVM8xVZsxVmcxVzMxV/8yAAMyAM8yAZsyAmcyAzMyA/8yqAMyqM8yqZsyqmcyqzMyq/8zVAMzVM8zVZszVmczVzMzV/8z/AMz/M8z/Zsz/mcz/zMz///8AAP8AM/8AZv8Amf8AzP8A//8rAP8rM/8rZv8rmf8rzP8r//9VAP9VM/9VZv9Vmf9VzP9V//+AAP+AM/+AZv+Amf+AzP+A//+qAP+qM/+qZv+qmf+qzP+q///VAP/VM//VZv/Vmf/VzP/V////AP//M///Zv//mf//zP///wAAAAAAAAAAAAAAACH5BAEAAPwALAAAAAAYABAAAAhKAAEIHEiwoEGB9g4qNGgv4cKHABxCVNhQoYSLGA9KPIgxI8GKDzte/DgRgEgJCDeGFAmypEmRLjWqjBmRZsGWNmvmTLkTYc+BAQEAOw==");

        private static byte[] Block(byte[] payload)
        {
            string length = payload.Length.ToString();
            return Encoding.ASCII.GetBytes("#" + length.Length + length).Concat(payload).Concat(new[] { (byte)'\n' }).ToArray();
        }

        private static readonly ToolCallContext Views = new ToolCallContext(null, showsViews: true);

        private static JObject ViewsCapabilities() => new JObject
        {
            ["extensions"] = new JObject { [McpAppViews.Extension] = new JObject { ["mimeTypes"] = new JArray(McpAppViews.MimeType) } }
        };

        // ---- the view itself -------------------------------------------------------------------------

        [Fact]
        public void TheScreenView_IsServed_WithTheMcpAppsClientInlined()
        {
            JObject read = McpAppViews.Read(McpAppViews.ScreenViewUri);
            var content = (JObject)read["contents"][0];
            string html = (string)content["text"];

            Assert.Equal(McpAppViews.MimeType, (string)content["mimeType"]);
            Assert.Contains("window.__gpibMcpApp=", html);                       // the client, rewritten to publish App
            Assert.DoesNotContain("/*@@MCP_APPS_SDK@@*/", html);
            Assert.DoesNotContain("export{", html.Replace(" ", ""));               // no module export left to break it
            // Nothing loaded from the network, so it works on a bench PC offline. (The client does contain
            // https:// strings - JSON-schema ids - but never loads them.)
            Assert.DoesNotContain("src=\"http", html);
            Assert.DoesNotContain("from \"http", html);
            Assert.DoesNotContain("import(", html);
            Assert.Contains("\"" + McpAppViews.ScreenImageTool + "\"", html);     // fetches through the view-only tool
            Assert.True(html.Length > 300000, "the client should be inlined; the page is " + html.Length + " chars");
        }

        [Fact]
        public void AnUnknownResource_IsNotServed()
        {
            Assert.Null(McpAppViews.Read("ui://gpib-mcp/nothing"));
        }

        [Fact]
        public void InlineSdk_PublishesTheExportedApp()
        {
            Assert.Equal("var a=1,b=2;window.__gpibMcpApp=a;", McpAppViews.InlineSdk("var a=1,b=2;export{b as X,a as App};\n"));
            Assert.Throws<InvalidOperationException>(() => McpAppViews.InlineSdk("var a=1;export{a as NotApp};"));
        }

        [Theory]
        [InlineData("{\"mimeTypes\":[\"text/html;profile=mcp-app\"]}", true)]
        [InlineData("{\"mimeTypes\":[\"text/plain\",\"TEXT/HTML;PROFILE=MCP-APP\"]}", true)]
        [InlineData("{}", false)]                                        // no mimeTypes
        [InlineData("{\"mimeTypes\":[\"application/x-other-ui\"]}", false)]  // a UI this view isn't
        [InlineData("{\"mimeTypes\":\"text/html;profile=mcp-app\"}", false)] // not an array
        public void ViewSupport_NeedsTheViewsMimeType(string extension, bool expected)
        {
            var caps = new JObject { ["extensions"] = new JObject { [McpAppViews.Extension] = JObject.Parse(extension) } };
            Assert.Equal(expected, McpAppViews.Declared(caps));
            Assert.False(McpAppViews.Declared(new JObject()));
            Assert.False(McpAppViews.Declared(null));
        }

        [Fact]
        public void AClientOfferingAnotherUiType_KeepsTheSvgFallback_AndDoesntSeeTheViewOnlyTool()
        {
            using (var d = Dispatcher())
            {
                Send(d, 1, "initialize", new JObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JObject { ["extensions"] = new JObject { [McpAppViews.Extension] = new JObject { ["mimeTypes"] = new JArray("application/x-other-ui") } } },
                    ["clientInfo"] = new JObject { ["name"] = "test", ["version"] = "1" }
                });
                var names = ((JArray)Send(d, 2, "tools/list")["result"]["tools"]).Select(t => (string)t["name"]);
                Assert.DoesNotContain(McpAppViews.ScreenImageTool, names);
            }
        }

        [Fact]
        public void TheSaveButton_DependsOnTheHostsDownloadCapability()
        {
            string html = (string)McpAppViews.Read(McpAppViews.ScreenViewUri)["contents"][0]["text"];
            Assert.Contains("getHostCapabilities()", html);
            Assert.Contains(".downloadFile;", html);
            Assert.Contains("role=\"status\"", html);   // loading -> shown / failed is announced to screen readers
        }

        // ---- the dispatcher --------------------------------------------------------------------------

        private static JObject Send(McpDispatcher dispatcher, int id, string method, JObject prms = null) =>
            (JObject)dispatcher.Dispatch(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms ?? new JObject() });

        private static McpDispatcher Dispatcher(IInstrumentManager visa = null) =>
            new McpDispatcher(InstrumentTools.BuildRegistry(visa ?? new FakeInstrumentManager()));

        private static JObject Initialize(McpDispatcher d, bool views) =>
            (JObject)Send(d, 1, "initialize", new JObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = views ? ViewsCapabilities() : new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "test", ["version"] = "1" }
            })["result"];

        [Fact]
        public void TheServer_AdvertisesResourcesAndTheUiExtension()
        {
            using (var d = Dispatcher())
            {
                JObject caps = (JObject)Initialize(d, views: false)["capabilities"];
                Assert.NotNull(caps["resources"]);
                Assert.Equal(McpAppViews.MimeType, (string)caps["extensions"][McpAppViews.Extension]["mimeTypes"][0]);

                var listed = (JArray)Send(d, 2, "resources/list")["result"]["resources"];
                Assert.Equal(McpAppViews.ScreenViewUri, (string)Assert.Single(listed)["uri"]);

                Assert.NotNull(Send(d, 3, "resources/read", new JObject { ["uri"] = McpAppViews.ScreenViewUri })["result"]["contents"]);
                Assert.NotNull(Send(d, 4, "resources/read", new JObject { ["uri"] = "ui://gpib-mcp/nothing" })["error"]);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CaptureTools_NameTheView_AndTheViewOnlyToolIsListedOnlyToClientsWithViews(bool views)
        {
            using (var d = Dispatcher())
            {
                Initialize(d, views);
                var tools = ((JArray)Send(d, 2, "tools/list")["result"]["tools"]).Cast<JObject>().ToDictionary(t => (string)t["name"]);

                foreach (string name in new[] { "instrument_capture_screen", "visa_query_binary" })
                    Assert.Equal(McpAppViews.ScreenViewUri, (string)tools[name]["_meta"]["ui"]["resourceUri"]);

                Assert.Equal(views, tools.ContainsKey(McpAppViews.ScreenImageTool));
                if (views) Assert.Equal("app", (string)tools[McpAppViews.ScreenImageTool]["_meta"]["ui"]["visibility"].Single());
            }
        }

        [Fact]
        public void TheViewOnlyTool_CantBeCalled_ByAClientWithoutViews()
        {
            using (var d = Dispatcher())
            {
                Initialize(d, views: false);
                JObject reply = Send(d, 2, "tools/call", new JObject
                {
                    ["name"] = McpAppViews.ScreenImageTool, ["arguments"] = new JObject { ["path"] = "x.png" }
                });
                Assert.Contains("unknown tool", (string)reply["error"]["message"]);
            }
        }

        [Theory]
        [InlineData(true, "inline under this tool call")]
        [InlineData(false, "attached below as PNG")]
        public void ToolsLearnWhetherTheClientShowsViews(bool views, string expected)
        {
            using (var d = Dispatcher(new FakeInstrumentManager { BinaryResponse = Block(Gif) }))
            {
                Initialize(d, views);
                JObject result = (JObject)Send(d, 2, "tools/call", new JObject
                {
                    ["name"] = "visa_query_binary",
                    ["arguments"] = new JObject { ["resource"] = "GPIB0::17::INSTR", ["command"] = ":HCOP:SDUM:DATA? GIF", ["save_dir"] = CaptureDir }
                })["result"];

                Assert.Contains(expected, (string)result["content"][0]["text"]);
            }
        }

        // ---- screen_view_image -----------------------------------------------------------------------

        private static McpTool Tool(string name, IInstrumentManager visa = null, InstrumentDatabase db = null, AssignmentStore store = null)
        {
            InstrumentTools.BuildRegistry(visa ?? new FakeInstrumentManager(), db ?? InstrumentDatabase.Empty(), store ?? AssignmentStore.InMemory())
                .TryGet(name, out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        [Fact]
        public void ScreenViewImage_ReturnsASavedImage_AsPng()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gpibmcp-view-" + Guid.NewGuid().ToString("N"));
            try
            {
                string saved = CaptureTools.SaveFile(new JObject { ["save_dir"] = dir }, "screen", ".gif", Gif);   // remembered as saved

                ToolOutput output = Tool(McpAppViews.ScreenImageTool).Invoke(new JObject { ["path"] = saved });

                Assert.False(output.IsError, output.AsText());
                var s = (JObject)output.Structured;
                byte[] png = Convert.FromBase64String((string)s["data"]);
                Assert.Equal("PNG", BinaryTools.Kind(png));
                Assert.Equal(24, (int)s["width"]);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void ScreenViewImage_RefusesFilesThisServerDidntSave()
        {
            string elsewhere = Path.Combine(Path.GetTempPath(), "gpibmcp-not-saved-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(elsewhere, Gif);
            try
            {
                Assert.True(Tool(McpAppViews.ScreenImageTool).Invoke(new JObject { ["path"] = elsewhere }).IsError);
                Assert.True(Tool(McpAppViews.ScreenImageTool).Invoke(new JObject { ["path"] = @"C:\Windows\win.ini" }).IsError);
            }
            finally { File.Delete(elsewhere); }
        }

        [Fact]
        public void ScreenViewImage_ServesTheCaptureFolder_ButOnlyImages()
        {
            // Claude Desktop runs more than one server process; the view's fetch may reach one that didn't save it.
            Assert.True(ScreenViewTools.MayServe(Path.Combine(CaptureTools.DefaultCaptureFolder(), "capture-8563E-20261013-145525.png")));
            Assert.False(ScreenViewTools.MayServe(Path.Combine(CaptureTools.DefaultCaptureFolder(), "notes.txt")));
            Assert.False(ScreenViewTools.MayServe(Path.Combine(CaptureTools.DefaultCaptureFolder(), "sub", "x.png")));
        }

        // ---- captures in a client that draws views ---------------------------------------------------

        private static (InstrumentDatabase, AssignmentStore) Model(InstrumentDefinition def, string resource)
        {
            var store = AssignmentStore.InMemory();
            store.Set(resource, def.Model);
            return (InstrumentDatabase.FromDefinitions(new[] { def }), store);
        }

        private static InstrumentDefinition E4406A() => new InstrumentDefinition
        {
            Model = "E4406A",
            Capture = new CaptureProfile { Method = "scpi_block", DumpCommand = ":HCOP:SDUM:DATA? GIF" },
            Commands = new List<InstrumentCommand>()
        };

        private static InstrumentDefinition Hp8563E() => new InstrumentDefinition
        {
            Model = "8563E",
            Capture = new CaptureProfile { Method = "hpgl", PlotCommand = "PLOT 550,279,9750,7479;", PreRoll = "SNGLS;TS;" },
            Commands = new List<InstrumentCommand>()
        };

        [Theory]
        [InlineData(null)]
        [InlineData("image")]
        public void AScpiCapture_WithViews_ShowsTheScreenInTheView_NotAnSvgArtifact(string format)
        {
            var (db, store) = Model(E4406A(), "GPIB0::17::INSTR");
            var args = new JObject { ["resource"] = "GPIB0::17::INSTR" };
            if (format != null) args["format"] = format;

            ToolOutput output = Tool("instrument_capture_screen", new FakeInstrumentManager { BinaryResponse = Block(Gif) }, db, store)
                .Invoke(args, Views);

            Assert.False(output.IsError, output.AsText());
            Assert.Contains("inline under this tool call", output.AsText());
            Assert.DoesNotContain("<svg", output.AsText());
            Assert.DoesNotContain("ACTION REQUIRED", output.AsText());
            Assert.Contains(output.Content, c => c.Kind == ToolContentKind.Image);
            Assert.EndsWith(".png", (string)((JObject)output.Structured)["png_saved_to"]);
        }

        [Fact]
        public void AScpiFileCapture_WithViews_IsStillShownInTheView_ButNotAttached()
        {
            var (db, store) = Model(E4406A(), "GPIB0::17::INSTR");

            ToolOutput output = Tool("instrument_capture_screen", new FakeInstrumentManager { BinaryResponse = Block(Gif) }, db, store)
                .Invoke(new JObject { ["resource"] = "GPIB0::17::INSTR", ["format"] = "file" }, Views);

            Assert.DoesNotContain(output.Content, c => c.Kind == ToolContentKind.Image);
            Assert.NotNull(((JObject)output.Structured)["png_saved_to"]);
        }

        [Fact]
        public void AnHpglCapture_WithViews_SkipsTheSvg()
        {
            var (db, store) = Model(Hp8563E(), "GPIB0::18::INSTR");

            ToolOutput output = Tool("instrument_capture_screen", new FakeInstrumentManager(), db, store)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["format"] = "plot" }, Views);

            Assert.False(output.IsError, output.AsText());
            Assert.Contains("inline under this tool call", output.AsText());
            Assert.DoesNotContain("<svg", output.AsText());
            Assert.DoesNotContain("fidelity", output.AsText());                 // fidelity is about the SVG, which isn't made
            Assert.Contains(output.Content, c => c.Kind == ToolContentKind.Image);
            Assert.NotNull(((JObject)output.Structured)["png_saved_to"]);
        }

        [Fact]
        public void AnHpglCapture_WithoutViews_IsUnchanged()
        {
            var (db, store) = Model(Hp8563E(), "GPIB0::18::INSTR");

            ToolOutput output = Tool("instrument_capture_screen", new FakeInstrumentManager(), db, store)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["format"] = "plot" });

            Assert.Contains("ACTION REQUIRED", output.AsText());
            Assert.Contains("<svg", output.AsText());
            Assert.Null(output.Structured);                                       // Claude Code would read only this
        }

        [Fact]
        public void AnAttachedImage_IsKeptUnderTheContentLimit()
        {
            var rng = new Random(1);
            int w = 800, h = 600, row = (w * 3 + 3) & ~3;
            var bmp = new byte[54 + row * h];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(54).CopyTo(bmp, 10);
            BitConverter.GetBytes(40).CopyTo(bmp, 14);
            BitConverter.GetBytes(w).CopyTo(bmp, 18);
            BitConverter.GetBytes(h).CopyTo(bmp, 22);
            BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
            BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
            for (int i = 54; i < bmp.Length; i++) bmp[i] = (byte)rng.Next(256);

            byte[] attached = CaptureTools.Attachable(Hpgl.Rendering.ScreenImage.ToPng(bmp));

            Assert.True(attached.Length <= BinaryTools.MaxImageContentBytes, attached.Length + " bytes");
            // As base64 inside a result, well under the ~150,000 characters at which Claude Desktop moves it aside.
            Assert.True(Convert.ToBase64String(attached).Length < 125000);
        }
    }
}
