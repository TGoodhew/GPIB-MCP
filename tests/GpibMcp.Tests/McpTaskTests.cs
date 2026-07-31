using System;
using System.Collections.Generic;
using System.Threading;
using GpibMcp.Mcp;
using GpibMcp.Mcp.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// The io.modelcontextprotocol/tasks extension and notifications/progress (#112): a slow tool answers
    /// immediately with a task handle the client polls, and reports its milestones on the way through.
    /// </summary>
    public class McpTaskTests
    {
        private const string TasksCapability = McpDispatcher.TasksExtension;

        /// <summary>Collects everything the dispatcher pushes at the client outside a response.</summary>
        private sealed class RecordingSink : IMcpMessageSink
        {
            private readonly List<JObject> _messages = new List<JObject>();
            public void Send(JObject message) { lock (_messages) _messages.Add(message); }
            public IReadOnlyList<JObject> Messages { get { lock (_messages) return _messages.ToArray(); } }
        }

        /// <summary>A long-running tool the test can hold open and release on demand.</summary>
        private sealed class GatedTool
        {
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Started = new ManualResetEventSlim(false);

            public McpTool Build(string name = "slow_tool")
            {
                return new McpTool(name, "blocks until released", null, (args, ctx) =>
                {
                    // Report first, then signal: a test that polls the moment Started fires must be certain
                    // the milestone is already recorded, or it races the worker's own status line.
                    ctx.Progress(1, 2, "halfway");
                    Started.Set();
                    Release.Wait(TimeSpan.FromSeconds(20));
                    ctx.Progress(2, 2, "done");
                    return ToolOutput.Text("finished " + name);
                });
            }
        }

        private static McpTool FastTool(string name = "fast_tool") =>
            new McpTool(name, "returns at once", null, (Func<JObject, ToolOutput>)(a => ToolOutput.Text("quick")));

        private static McpTool ProgressTool(string name = "progress_tool") =>
            new McpTool(name, "reports progress", null, (args, ctx) =>
            {
                ctx.Progress(1, 3, "one");
                ctx.Progress(2, 3, "two");
                ctx.Progress(2, 3, "repeat - must be dropped");   // not increasing
                ctx.Progress(3, 3, "three");
                return ToolOutput.Text("ok");
            });

        private static JObject Call(string tool, JObject meta = null, int id = 7)
        {
            var prms = new JObject { ["name"] = tool, ["arguments"] = new JObject() };
            if (meta != null) prms["_meta"] = meta;
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = prms };
        }

        private static JObject Initialize(bool declareTasks)
        {
            var capabilities = new JObject();
            if (declareTasks)
                capabilities["extensions"] = new JObject { [TasksCapability] = new JObject() };

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JObject
                {
                    ["protocolVersion"] = McpDispatcher.ProtocolVersion,
                    ["capabilities"] = capabilities,
                    ["clientInfo"] = new JObject { ["name"] = "test", ["version"] = "1.0" }
                }
            };
        }

        /// <summary>The per-request client-capability declaration MCP 2026-07-28 uses instead of a handshake.</summary>
        private static JObject TaskCapableMeta() =>
            new JObject
            {
                ["io.modelcontextprotocol/clientCapabilities"] =
                    new JObject { ["extensions"] = new JObject { [TasksCapability] = new JObject() } }
            };

        private static JObject TaskRpc(string method, string taskId, int id = 9) =>
            new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = new JObject { ["taskId"] = taskId }
            };

        /// <summary>Polls tasks/get until the task is terminal (or the wait runs out) and returns the last result.</summary>
        private static JObject PollUntilTerminal(McpDispatcher dispatcher, string taskId, int timeoutMs = 20000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            JObject result;
            while (true)
            {
                result = (JObject)dispatcher.Dispatch(TaskRpc("tasks/get", taskId))["result"];
                string status = (string)result["status"];
                if (status != ServerTask.StatusWorking || DateTime.UtcNow > deadline) return result;
                Thread.Sleep(15);
            }
        }

        // ---------------------------------------------------------------- progress

        [Fact]
        public void Progress_IsSentAsNotifications_WhenTheClientAsksForIt()
        {
            var sink = new RecordingSink();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(ProgressTool())))
            {
                dispatcher.Notifications = sink;
                var meta = new JObject { ["progressToken"] = "abc123" };
                dispatcher.Dispatch(Call("progress_tool", meta));
            }

            var progress = sink.Messages;
            // The repeated value is dropped: the spec requires progress to increase with each notification.
            Assert.Equal(3, progress.Count);
            Assert.All(progress, m => Assert.Equal("notifications/progress", (string)m["method"]));
            Assert.All(progress, m => Assert.Equal("abc123", (string)m["params"]["progressToken"]));
            Assert.Equal(new[] { 1.0, 2.0, 3.0 },
                         new[] { (double)progress[0]["params"]["progress"],
                                 (double)progress[1]["params"]["progress"],
                                 (double)progress[2]["params"]["progress"] });
            Assert.Equal(3.0, (double)progress[0]["params"]["total"]);
            Assert.Equal("one", (string)progress[0]["params"]["message"]);
        }

        [Fact]
        public void Progress_IsSilent_WhenTheClientSuppliedNoToken()
        {
            var sink = new RecordingSink();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(ProgressTool())))
            {
                dispatcher.Notifications = sink;
                dispatcher.Dispatch(Call("progress_tool"));
            }
            Assert.Empty(sink.Messages);
        }

        [Fact]
        public void Progress_IsDropped_WhenTheTransportHasNoOutboundChannel()
        {
            // The HTTP transport is exactly this case: one response per POST, nowhere to put a notification.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(ProgressTool())))
            {
                var meta = new JObject { ["progressToken"] = 42 };
                JObject response = dispatcher.Dispatch(Call("progress_tool", meta));
                // No sink attached: the call still succeeds, the milestones simply go nowhere.
                Assert.Equal("ok", (string)response["result"]["content"][0]["text"]);
            }
        }

        // ---------------------------------------------------------------- task creation gate

        [Fact]
        public void LongRunningTool_RunsSynchronously_ForAClientThatDidNotDeclareTheExtension()
        {
            var gate = new GatedTool();
            gate.Release.Set();   // let it finish immediately
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(gate.Build())))
            {
                dispatcher.Dispatch(Initialize(declareTasks: false));
                JObject result = (JObject)dispatcher.Dispatch(Call("slow_tool"))["result"];

                Assert.Null(result["taskId"]);
                Assert.Equal("finished slow_tool", (string)result["content"][0]["text"]);
                Assert.Equal(0, dispatcher.Tasks.Count);
            }
        }

        [Fact]
        public void FastTool_NeverBecomesATask_EvenForATaskCapableClient()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(FastTool())))
            {
                dispatcher.Dispatch(Initialize(declareTasks: true));
                JObject result = (JObject)dispatcher.Dispatch(Call("fast_tool"))["result"];

                Assert.Null(result["taskId"]);
                Assert.Equal("quick", (string)result["content"][0]["text"]);
            }
        }

        [Fact]
        public void LongRunningTool_ReturnsATaskHandle_WhenTheClientDeclaredAtInitialize()
        {
            var gate = new GatedTool();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(gate.Build())))
            {
                dispatcher.Dispatch(Initialize(declareTasks: true));
                JObject result = (JObject)dispatcher.Dispatch(Call("slow_tool"))["result"];

                Assert.Equal("task", (string)result["resultType"]);
                Assert.Equal(ServerTask.StatusWorking, (string)result["status"]);
                Assert.False(string.IsNullOrEmpty((string)result["taskId"]));
                Assert.Equal(TaskStore.DefaultPollIntervalMs, (int)result["pollIntervalMs"]);
                Assert.Equal(TaskStore.DefaultTtlMs, (int)result["ttlMs"]);
                Assert.NotNull(result["createdAt"]);
                Assert.NotNull(result["lastUpdatedAt"]);

                gate.Release.Set();
                PollUntilTerminal(dispatcher, (string)result["taskId"]);
            }
        }

        [Fact]
        public void LongRunningTool_ReturnsATaskHandle_WhenTheRequestItselfDeclaresSupport()
        {
            // MCP 2026-07-28 has no handshake: the declaration rides in each request's _meta.
            var gate = new GatedTool();
            gate.Release.Set();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(gate.Build())))
            {
                JObject result = (JObject)dispatcher.Dispatch(Call("slow_tool", TaskCapableMeta()))["result"];
                Assert.Equal("task", (string)result["resultType"]);
                PollUntilTerminal(dispatcher, (string)result["taskId"]);
            }
        }

        [Fact]
        public void Initialize_AdvertisesTheTasksExtension()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(FastTool())))
            {
                JObject result = (JObject)dispatcher.Dispatch(Initialize(declareTasks: false))["result"];
                Assert.NotNull(result["capabilities"]["extensions"][TasksCapability]);
            }
        }

        // ---------------------------------------------------------------- polling

        [Fact]
        public void TasksGet_IsAnsweredWhileTheToolIsStillOnTheHardware()
        {
            // The whole point of the extension: a poll must not queue behind the 24-second capture.
            var gate = new GatedTool();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(gate.Build())))
            {
                string taskId = (string)dispatcher.Dispatch(Call("slow_tool", TaskCapableMeta()))["result"]["taskId"];
                Assert.True(gate.Started.Wait(TimeSpan.FromSeconds(5)), "the task never started");

                JObject working = (JObject)dispatcher.Dispatch(TaskRpc("tasks/get", taskId))["result"];
                Assert.Equal(ServerTask.StatusWorking, (string)working["status"]);
                Assert.Equal("complete", (string)working["resultType"]);
                Assert.Null(working["result"]);
                // Progress from a task lands in statusMessage - there is no open request to notify on.
                Assert.Equal("halfway", (string)working["statusMessage"]);

                gate.Release.Set();
                JObject done = PollUntilTerminal(dispatcher, taskId);
                Assert.Equal(ServerTask.StatusCompleted, (string)done["status"]);
                Assert.Equal("finished slow_tool", (string)done["result"]["content"][0]["text"]);
            }
        }

        [Fact]
        public void TasksGet_RejectsAnUnknownTaskId()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(FastTool())))
            {
                JObject response = dispatcher.Dispatch(TaskRpc("tasks/get", "task-does-not-exist"));
                Assert.Equal(-32602, (int)response["error"]["code"]);
            }
        }

        [Fact]
        public void TasksGet_RequiresATaskId()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(FastTool())))
            {
                var request = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tasks/get", ["params"] = new JObject()
                };
                Assert.Equal(-32602, (int)dispatcher.Dispatch(request)["error"]["code"]);
            }
        }

        [Fact]
        public void FailedTool_SettlesTheTaskAsCompletedWithAnErrorResult()
        {
            // A tool that throws is an isError result, not a JSON-RPC error - so the task completed, and the
            // model reads the failure text exactly as it would from a synchronous call.
            var registry = new ToolRegistry().Add(new McpTool("boom", "throws", null,
                (args, ctx) => throw new InvalidOperationException("instrument said no")));

            using (var dispatcher = new McpDispatcher(registry))
            {
                string taskId = (string)dispatcher.Dispatch(Call("boom", TaskCapableMeta()))["result"]["taskId"];
                JObject done = PollUntilTerminal(dispatcher, taskId);

                Assert.Equal(ServerTask.StatusCompleted, (string)done["status"]);
                Assert.True((bool)done["result"]["isError"]);
                Assert.Contains("instrument said no", (string)done["result"]["content"][0]["text"]);
            }
        }

        // ---------------------------------------------------------------- cancel / update

        [Fact]
        public void TasksCancel_StopsATaskThatHasNotReachedTheHardware()
        {
            // One worker thread runs tasks in order, so the second task is still queued while the first
            // holds the bus - and a queued task is the one case cancellation can genuinely honour.
            var gate = new GatedTool();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(gate.Build())))
            {
                string first = (string)dispatcher.Dispatch(Call("slow_tool", TaskCapableMeta(), 11))["result"]["taskId"];
                Assert.True(gate.Started.Wait(TimeSpan.FromSeconds(5)), "the first task never started");
                string second = (string)dispatcher.Dispatch(Call("slow_tool", TaskCapableMeta(), 12))["result"]["taskId"];

                JObject ack = (JObject)dispatcher.Dispatch(TaskRpc("tasks/cancel", second))["result"];
                Assert.Equal("complete", (string)ack["resultType"]);

                gate.Release.Set();
                Assert.Equal(ServerTask.StatusCancelled, (string)PollUntilTerminal(dispatcher, second)["status"]);
                Assert.Equal(ServerTask.StatusCompleted, (string)PollUntilTerminal(dispatcher, first)["status"]);
            }
        }

        [Fact]
        public void TasksCancel_AndTasksUpdate_RejectUnknownTasks()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(FastTool())))
            {
                Assert.Equal(-32602, (int)dispatcher.Dispatch(TaskRpc("tasks/cancel", "nope"))["error"]["code"]);
                Assert.Equal(-32602, (int)dispatcher.Dispatch(TaskRpc("tasks/update", "nope"))["error"]["code"]);
            }
        }

        [Fact]
        public void TasksUpdate_IsAcknowledged_EvenThoughThisServerNeverAsksForInput()
        {
            var gate = new GatedTool();
            gate.Release.Set();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(gate.Build())))
            {
                string taskId = (string)dispatcher.Dispatch(Call("slow_tool", TaskCapableMeta()))["result"]["taskId"];
                JObject ack = (JObject)dispatcher.Dispatch(TaskRpc("tasks/update", taskId))["result"];
                Assert.Equal("complete", (string)ack["resultType"]);
                PollUntilTerminal(dispatcher, taskId);
            }
        }

        // ---------------------------------------------------------------- store behaviour

        [Fact]
        public void TaskStore_ForgetsTasksOnceTheirTtlHasElapsed()
        {
            var store = new TaskStore();
            ServerTask task = store.Create("tools/call x", ttlMs: 0);
            task.Complete(new JObject());

            Thread.Sleep(5);
            ServerTask found;
            Assert.False(store.TryGet(task.TaskId, out found));
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void TaskStore_KeepsATaskWithNoTtlForever()
        {
            var store = new TaskStore();
            ServerTask task = store.Create("tools/call x", ttlMs: null);
            ServerTask found;
            Assert.True(store.TryGet(task.TaskId, out found));
            Assert.Same(task, found);
            Assert.Null(task.ToCreateResult()["ttlMs"].Value<int?>());
        }

        [Fact]
        public void ServerTask_TerminalStateIsFinal()
        {
            var task = new ServerTask("t1", "tools/call x", 1000, 500);
            task.Complete(new JObject { ["content"] = new JArray() }, "done");
            task.Fail(new JObject { ["code"] = -1 }, "too late");
            task.SetStatusMessage("also too late");

            Assert.Equal(ServerTask.StatusCompleted, task.Status);
            Assert.Equal("done", (string)task.ToDetailedResult()["statusMessage"]);
            Assert.Null(task.ToDetailedResult()["error"]);
        }
    }
}
