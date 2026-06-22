using System;
using System.IO;
using System.Reflection;
using GpibMcp.Diagnostics;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Selects and constructs the GPIB backend (<see cref="IGpibTransport"/>) from configuration.
    /// The backend is chosen by the <c>GPIB_MCP_BACKEND</c> environment variable; NI-VISA is the
    /// default so nothing changes for existing users. Backends are loaded by assembly name at runtime
    /// so this core has no compile-time dependency on any backend (and thus none on NI) - that is the
    /// point of the split (issue #22). Additional backends (Prologix, AR488) plug in here.
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
                    return Load("GpibMcp.NiVisa", "GpibMcp.Instruments.NiVisaTransport");
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

        /// <summary>
        /// Loads a backend assembly (from next to this assembly, falling back to the default probing
        /// path) and instantiates its <see cref="IGpibTransport"/>. Kept reflection-based so the core
        /// never compile-links a backend - a non-NI build/run is possible when another backend is chosen.
        /// </summary>
        private static IGpibTransport Load(string assemblyName, string typeName)
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(TransportFactory).Assembly.Location);
                string path = dir == null ? null : Path.Combine(dir, assemblyName + ".dll");
                Assembly asm = (path != null && File.Exists(path)) ? Assembly.LoadFrom(path) : Assembly.Load(assemblyName);
                Type type = asm.GetType(typeName, throwOnError: true);
                return (IGpibTransport)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not load the GPIB backend '" + assemblyName + "'. Build the full solution " +
                    "(GPIB-MCP.sln) so the backend assembly is deployed next to the server, and ensure its " +
                    "driver (e.g. NI-VISA for the nivisa backend) is installed. Detail: " + ex.Message, ex);
            }
        }
    }
}
