using System;
using System.Collections.Generic;
using System.Linq;

namespace GpibMcp.Mcp.Tasks
{
    /// <summary>
    /// The server's live tasks, keyed by task id (#112). In-memory only: a task outlives the request that
    /// created it, but not the server process - the extension calls a task id "durable" so a client can keep
    /// polling across a reconnect, and for a stdio server the client's reconnect *is* a new server, so there
    /// would be nothing to resume anyway.
    ///
    /// Entries are dropped once their TTL elapses, and the oldest terminal tasks are evicted if a client
    /// somehow never stops creating them - a bounded store, so a long session cannot grow without limit.
    /// </summary>
    public sealed class TaskStore
    {
        /// <summary>How long a task remains pollable after creation. Generous next to a 24 s capture.</summary>
        public const int DefaultTtlMs = 10 * 60 * 1000;

        /// <summary>Polling cadence suggested to clients - fine enough to feel live on a 7-24 s capture.</summary>
        public const int DefaultPollIntervalMs = 1000;

        /// <summary>Hard ceiling on retained tasks; the oldest terminal ones go first.</summary>
        public const int MaxTasks = 100;

        private readonly Dictionary<string, ServerTask> _tasks = new Dictionary<string, ServerTask>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private long _sequence;

        /// <summary>Creates a task in the <c>working</c> state and returns it.</summary>
        public ServerTask Create(string method, int? ttlMs = DefaultTtlMs, int pollIntervalMs = DefaultPollIntervalMs)
        {
            lock (_gate)
            {
                Sweep(DateTime.UtcNow);
                string id = "task-" + (++_sequence).ToString("D4") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var task = new ServerTask(id, method, ttlMs, pollIntervalMs);
                _tasks[id] = task;
                Evict();
                return task;
            }
        }

        /// <summary>Looks up a live task. Expired tasks are gone and report as not found.</summary>
        public bool TryGet(string taskId, out ServerTask task)
        {
            lock (_gate)
            {
                Sweep(DateTime.UtcNow);
                return _tasks.TryGetValue(taskId ?? string.Empty, out task);
            }
        }

        /// <summary>Number of retained tasks (after expiry).</summary>
        public int Count
        {
            get { lock (_gate) { Sweep(DateTime.UtcNow); return _tasks.Count; } }
        }

        private void Sweep(DateTime nowUtc)
        {
            List<string> expired = null;
            foreach (var pair in _tasks)
            {
                if (!pair.Value.IsExpired(nowUtc)) continue;
                if (expired == null) expired = new List<string>();
                expired.Add(pair.Key);
            }
            if (expired == null) return;
            foreach (string id in expired) _tasks.Remove(id);
        }

        private void Evict()
        {
            if (_tasks.Count <= MaxTasks) return;
            // Terminal tasks have nothing left to deliver, so they go before anything still running.
            foreach (var victim in _tasks.Values
                         .Where(t => t.IsTerminal)
                         .OrderBy(t => t.CreatedAtUtc)
                         .Take(_tasks.Count - MaxTasks)
                         .Select(t => t.TaskId)
                         .ToList())
            {
                _tasks.Remove(victim);
            }
        }
    }
}
