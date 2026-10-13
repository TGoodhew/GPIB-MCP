using System.Collections.Generic;
using System.Text.RegularExpressions;
using GpibMcp.Instruments;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Reads an instrument's error queue until it is empty, using the model's own commands
    /// (<see cref="IdentitySpec.ErrorQuery"/>, then <see cref="IdentitySpec.ClearStatusQuery"/>). Shared by
    /// instrument_identify, which clears the error a rejected *IDN? leaves (#144), and the SRQ waiter, which
    /// clears an error already queued before an operation so one at completion is the operation's own.
    /// </summary>
    internal static class ErrorQueue
    {
        /// <summary>Most reads of an error queue, for instruments that report one error per read.</summary>
        public const int MaxReads = 5;

        public sealed class Result
        {
            /// <summary>The errors read, in order; none when the queue was already empty.</summary>
            public readonly List<string> Errors = new List<string>();

            /// <summary>
            /// Whether a no-error reply was read - the queue is empty. False when it still held errors after
            /// <see cref="MaxReads"/> reads: then nothing is claimed cleared and the status byte is left alone.
            /// </summary>
            public bool Emptied;

            /// <summary>The commands sent, for messages: "ERR?" or "ERR? and STB?".</summary>
            public string Sent;
        }

        /// <summary>
        /// Reads <paramref name="identity"/>'s error query until a no-error reply; then, if any errors were read
        /// and the model has one, its clear-status query - on the 8563E the status byte's error bit can stay
        /// latched after the queue is empty, until STB? reads it.
        /// </summary>
        public static Result Clear(IInstrumentManager visa, string resource, IoSpec io, IdentitySpec identity)
        {
            var result = new Result { Sent = identity.ErrorQuery };
            for (int i = 0; i < MaxReads; i++)
            {
                string reply = (visa.Query(resource, identity.ErrorQuery, io) ?? "").Trim();
                if (IsNoError(reply)) { result.Emptied = true; break; }
                result.Errors.Add(reply);
            }

            if (result.Emptied && result.Errors.Count > 0 && !string.IsNullOrEmpty(identity.ClearStatusQuery))
            {
                visa.Query(resource, identity.ClearStatusQuery, io);
                result.Sent += " and " + identity.ClearStatusQuery;
            }
            return result;
        }

        /// <summary>An error-queue reply that means "no error": empty, or 0 / +0 (alone or before a message).</summary>
        public static bool IsNoError(string reply) =>
            reply.Length == 0 || Regex.IsMatch(reply, @"^\+?0+(\s*,|\s*$)");
    }
}
