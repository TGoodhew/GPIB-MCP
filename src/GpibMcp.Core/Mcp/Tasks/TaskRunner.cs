using System;
using System.Collections.Concurrent;
using System.Threading;
using GpibMcp.Diagnostics;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp.Tasks
{
    /// <summary>
    /// Executes task-backed requests off the dispatch thread (#112), on <b>one</b> worker thread.
    ///
    /// Single-threaded on purpose, twice over. The GPIB bus is not re-entrant, so two captures must never
    /// overlap; and a queue of one keeps the "which request is on the hardware right now" answer as simple as
    /// it is today. Returning a task handle was never about making the instrument faster - it is about not
    /// making the client sit silent for 24 seconds - so serial execution costs nothing we had.
    ///
    /// The work item the dispatcher hands over takes the tool lock itself, which is what keeps a background
    /// task and a foreground <c>tools/call</c> from colliding on the bus.
    /// </summary>
    public sealed class TaskRunner : IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new BlockingCollection<WorkItem>();
        private readonly Thread _worker;

        public TaskRunner()
        {
            _worker = new Thread(Pump)
            {
                IsBackground = true,   // never holds up process exit
                Name = "mcp-task-runner"
            };
            _worker.Start();
        }

        /// <summary>
        /// Queues <paramref name="work"/> to run on the worker thread and settle <paramref name="task"/>.
        /// Returns immediately - the caller answers the client with the task handle.
        /// </summary>
        public void Enqueue(ServerTask task, Func<JObject> work)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (work == null) throw new ArgumentNullException(nameof(work));
            try { _queue.Add(new WorkItem { Task = task, Work = work }); }
            catch (InvalidOperationException)
            {
                // Shutting down: settle the task rather than leave a client polling a ghost.
                task.Fail(new JObject { ["code"] = -32603, ["message"] = "Server is shutting down" },
                          "The server stopped before this task could run.");
            }
        }

        private void Pump()
        {
            foreach (WorkItem item in _queue.GetConsumingEnumerable())
            {
                ServerTask task = item.Task;
                try
                {
                    // Cancelled while it sat in the queue: nothing has touched the hardware, so this one
                    // cancellation we can actually honour.
                    if (task.CancelRequested)
                    {
                        task.Cancel("Cancelled before execution started.");
                        continue;
                    }

                    JObject result = item.Work();
                    // Replace the running message: a completed task showing "Running …" reads as stale.
                    task.Complete(result, "Completed.");
                }
                catch (McpError mcp)
                {
                    Log.Warn("Task " + task.TaskId + " failed: " + mcp.Message);
                    var error = new JObject { ["code"] = mcp.Code, ["message"] = mcp.Message };
                    if (mcp.ErrorData != null) error["data"] = mcp.ErrorData;
                    task.Fail(error, mcp.Message);
                }
                catch (Exception ex)
                {
                    Log.Error("Task " + task.TaskId + " failed", ex);
                    task.Fail(new JObject { ["code"] = -32603, ["message"] = "Internal error: " + ex.Message },
                              ex.Message);
                }
            }
        }

        public void Dispose()
        {
            try { _queue.CompleteAdding(); } catch { /* already done */ }
            // Give the in-flight item a moment to settle; the thread is a background one either way.
            try { _worker.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
            try { _queue.Dispose(); } catch { /* best effort */ }
        }

        private sealed class WorkItem
        {
            public ServerTask Task;
            public Func<JObject> Work;
        }
    }
}
