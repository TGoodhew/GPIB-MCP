using System;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp.Tasks
{
    /// <summary>
    /// One task's durable state, as defined by the <c>io.modelcontextprotocol/tasks</c> extension (#112).
    /// A task is created when the server elects to run a slow request asynchronously; the client polls
    /// <c>tasks/get</c> until the status is terminal and then reads <c>result</c> (or <c>error</c>).
    /// </summary>
    public sealed class ServerTask
    {
        /// <summary>The task lifecycle states. <c>completed</c>, <c>failed</c> and <c>cancelled</c> are terminal.</summary>
        public const string StatusWorking = "working";
        public const string StatusInputRequired = "input_required";
        public const string StatusCompleted = "completed";
        public const string StatusFailed = "failed";
        public const string StatusCancelled = "cancelled";

        private readonly object _gate = new object();

        private string _status = StatusWorking;
        private string _statusMessage;
        private DateTime _lastUpdatedUtc;
        private JObject _result;
        private JObject _error;
        private volatile bool _cancelRequested;

        public ServerTask(string taskId, string method, int? ttlMs, int pollIntervalMs)
        {
            TaskId = taskId;
            Method = method;
            TtlMs = ttlMs;
            PollIntervalMs = pollIntervalMs;
            CreatedAtUtc = DateTime.UtcNow;
            _lastUpdatedUtc = CreatedAtUtc;
        }

        public string TaskId { get; }

        /// <summary>The request that created this task (e.g. <c>tools/call instrument_capture_screen</c>) - diagnostics only.</summary>
        public string Method { get; }

        public DateTime CreatedAtUtc { get; }
        public int? TtlMs { get; }
        public int PollIntervalMs { get; }

        public string Status { get { lock (_gate) return _status; } }

        /// <summary>True once the task has reached a state it can never leave.</summary>
        public bool IsTerminal
        {
            get
            {
                lock (_gate)
                    return _status == StatusCompleted || _status == StatusFailed || _status == StatusCancelled;
            }
        }

        /// <summary>The client asked for cancellation (cooperative - see <see cref="ToolCallContext"/>).</summary>
        public bool CancelRequested => _cancelRequested;

        public void RequestCancel() { _cancelRequested = true; }

        /// <summary>Updates the human-readable status line without leaving the current state.</summary>
        public void SetStatusMessage(string message)
        {
            lock (_gate)
            {
                if (_status != StatusWorking) return;   // never talk over a terminal state
                _statusMessage = message;
                _lastUpdatedUtc = DateTime.UtcNow;
            }
        }

        public void Complete(JObject result, string message = null)
        {
            Terminate(StatusCompleted, message, result, null);
        }

        public void Fail(JObject error, string message = null)
        {
            Terminate(StatusFailed, message, null, error);
        }

        public void Cancel(string message = null)
        {
            Terminate(StatusCancelled, message, null, null);
        }

        private void Terminate(string status, string message, JObject result, JObject error)
        {
            lock (_gate)
            {
                if (_status == StatusCompleted || _status == StatusFailed || _status == StatusCancelled) return;
                _status = status;
                if (message != null) _statusMessage = message;
                _result = result;
                _error = error;
                _lastUpdatedUtc = DateTime.UtcNow;
            }
        }

        /// <summary>True when the task has outlived its TTL and may be discarded.</summary>
        public bool IsExpired(DateTime nowUtc)
        {
            if (!TtlMs.HasValue) return false;
            return (nowUtc - CreatedAtUtc).TotalMilliseconds > TtlMs.Value;
        }

        /// <summary>
        /// The <c>CreateTaskResult</c> returned in place of the original request's result: the task fields
        /// inlined at the top level with <c>resultType: "task"</c>, which is how a client tells the two apart.
        /// </summary>
        public JObject ToCreateResult()
        {
            JObject json = BaseJson();
            json["resultType"] = "task";
            return json;
        }

        /// <summary>
        /// The <c>tasks/get</c> result: the same fields plus, in a terminal state, the original request's
        /// <c>result</c> or the JSON-RPC <c>error</c> that ended it. <c>resultType</c> is "complete" here -
        /// the poll itself succeeded, whatever the task's own outcome.
        /// </summary>
        public JObject ToDetailedResult()
        {
            JObject json = BaseJson();
            json["resultType"] = "complete";
            lock (_gate)
            {
                if (_result != null) json["result"] = _result;
                if (_error != null) json["error"] = _error;
            }
            return json;
        }

        private JObject BaseJson()
        {
            lock (_gate)
            {
                var json = new JObject
                {
                    ["taskId"] = TaskId,
                    ["status"] = _status,
                    ["createdAt"] = Iso(CreatedAtUtc),
                    ["lastUpdatedAt"] = Iso(_lastUpdatedUtc),
                    ["ttlMs"] = TtlMs.HasValue ? (JToken)TtlMs.Value : JValue.CreateNull(),
                    ["pollIntervalMs"] = PollIntervalMs
                };
                if (_statusMessage != null) json["statusMessage"] = _statusMessage;
                return json;
            }
        }

        private static string Iso(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }
}
