using GpibMcp.Instruments;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Resolves the per-call <see cref="IoSpec"/> for an instrument from its assigned model's
    /// definition - the read/write terminators and the model's optional default bounded-read size -
    /// so queries and reads honour each instrument's I/O conventions (issue #35). An unassigned or
    /// unknown resource yields a plain timeout-only spec, preserving the historical behaviour.
    /// </summary>
    internal static class InstrumentIo
    {
        /// <summary>Builds the I/O spec for <paramref name="resource"/> via its assignment, with an optional per-call bounded-read override.</summary>
        public static IoSpec Resolve(InstrumentDatabase db, AssignmentStore assignments, string resource,
                                     int timeoutMs, int readBytesOverride = 0)
        {
            InstrumentDefinition def = null;
            string model = assignments != null ? assignments.Get(resource) : null;
            if (!string.IsNullOrEmpty(model) && db != null) db.TryGet(model, out def);
            return FromDefinition(def, timeoutMs, readBytesOverride);
        }

        /// <summary>Builds the I/O spec from a definition directly (used where the model is already resolved).</summary>
        public static IoSpec FromDefinition(InstrumentDefinition def, int timeoutMs, int readBytesOverride = 0)
        {
            var io = new IoSpec(timeoutMs);
            if (def != null)
            {
                if (def.Termination != null)
                {
                    io.ReadTermChar = def.Termination.ReadTerminatorChar();
                    io.WriteTerminator = def.Termination.Write;
                }
                if (def.MaxReadBytes.GetValueOrDefault() > 0)
                    io.MaxReadBytes = def.MaxReadBytes.Value;
            }
            // A per-call read_bytes argument wins over the model default (and is the only source for
            // an unassigned instrument): purely an opt-in safeguard for a streaming/timeout case.
            if (readBytesOverride > 0) io.MaxReadBytes = readBytesOverride;
            return io;
        }
    }
}
