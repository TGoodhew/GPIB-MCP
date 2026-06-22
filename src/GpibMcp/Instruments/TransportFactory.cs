using System;
using GpibMcp.Diagnostics;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Selects and constructs the GPIB backend (<see cref="IGpibTransport"/>) from configuration.
    /// The backend is chosen by the <c>GPIB_MCP_BACKEND</c> environment variable; NI-VISA is the
    /// default so nothing changes for existing users. Additional backends (Prologix, AR488) plug in
    /// here once implemented (issue #22).
    /// </summary>
    public static class TransportFactory
    {
        public const string BackendEnvVar = "GPIB_MCP_BACKEND";
        public const string DefaultBackend = "nivisa";

        /// <summary>Creates the configured transport (NI-VISA unless <c>GPIB_MCP_BACKEND</c> says otherwise).</summary>
        public static IGpibTransport Create()
        {
            string backend = Environment.GetEnvironmentVariable(BackendEnvVar);
            if (string.IsNullOrWhiteSpace(backend)) backend = DefaultBackend;
            backend = backend.Trim().ToLowerInvariant();

            Log.Info("GPIB backend: " + backend);
            switch (backend)
            {
                case "nivisa":
                case "ni":
                case "visa":
                    return new NiVisaTransport();
                case "prologix":
                case "ar488":
                    throw new NotSupportedException("GPIB backend '" + backend + "' is not implemented yet. " +
                        "The transport abstraction is in place (issue #22); the " + backend +
                        " backend is a follow-up. Set " + BackendEnvVar + "=nivisa (the default) for now.");
                default:
                    throw new NotSupportedException("Unknown GPIB backend '" + backend + "'. " +
                        "Set " + BackendEnvVar + " to one of: nivisa (default).");
            }
        }
    }
}
