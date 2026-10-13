using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using GpibMcp.Instruments;

namespace GpibMcp.Setup
{
    /// <summary>
    /// Whether the NI drivers this build needs are on this PC - asked the way the server itself will ask.
    ///
    /// "Is NI installed" is the wrong question. The backend is compiled against the NI assemblies on the
    /// machine that built it, and .NET Framework binds a strong-named reference to that version or, through
    /// NI's publisher policy, a newer one. A PC with an older NI-VISA has NI installed and still cannot load
    /// the backend. So this loads exactly what <c>GpibMcp.NiVisa.dll</c> references, through the same binder
    /// the server uses, and loads the two native driver DLLs in this (32-bit) process. What it reports is what
    /// startup would find, by product name, with what to install.
    /// </summary>
    public sealed class NiCheck
    {
        public const string NiVisaDownload = "https://www.ni.com/en/support/downloads/drivers/download.ni-visa.html";
        public const string Ni4882Download = "https://www.ni.com/en/support/downloads/drivers/download.ni-488-2.html";

        public const string BackendAssembly = "GpibMcp.NiVisa";

        private readonly string _exeDir;
        private readonly Func<AssemblyName, Assembly> _load;
        private readonly Func<string, bool> _loadNative;
        private readonly Func<string, IReadOnlyList<Version>> _installedVersions;
        private readonly Func<string> _startBackend;

        public NiCheck(string exeDir)
            : this(exeDir, Assembly.Load, LoadNative, GacVersions, StartBackend) { }

        /// <summary>The seams, for tests: how assemblies, native DLLs and the backend are loaded.</summary>
        public NiCheck(string exeDir, Func<AssemblyName, Assembly> load, Func<string, bool> loadNative,
                       Func<string, IReadOnlyList<Version>> installedVersions, Func<string> startBackend)
        {
            _exeDir = exeDir;
            _load = load;
            _loadNative = loadNative;
            _installedVersions = installedVersions;
            _startBackend = startBackend;
        }

        public enum Status { Ok, Missing, TooOld, Failed }

        public sealed class Component
        {
            public Component(string name, string product, Status status, string detail)
            {
                Name = name;
                Product = product;
                Status = status;
                Detail = detail;
            }

            /// <summary>What it is, in the words NI's installers use.</summary>
            public string Name { get; }

            /// <summary>The NI product that provides it: "NI-VISA", "NI-488.2", or null for GPIB MCP itself.</summary>
            public string Product { get; }

            public Status Status { get; }
            public string Detail { get; }
            public bool Ok => Status == Status.Ok;
        }

        public sealed class Result
        {
            public Result(IReadOnlyList<Component> components) { Components = components; }

            public IReadOnlyList<Component> Components { get; }

            public bool Ok => Components.All(c => c.Ok);

            /// <summary>The NI products that need installing or updating, in a stable order.</summary>
            public IReadOnlyList<string> ProductsNeeded =>
                Components.Where(c => !c.Ok && c.Product != null).Select(c => c.Product).Distinct()
                          .OrderBy(p => p, StringComparer.Ordinal).ToList();

            public string Report()
            {
                var text = new StringBuilder();
                foreach (Component c in Components)
                {
                    string label = c.Status == Status.Ok ? "OK" : c.Status == Status.TooOld ? "TOO OLD" :
                                   c.Status == Status.Missing ? "MISSING" : "FAILED";
                    text.Append("  ").Append(label.PadRight(9)).Append(c.Name);
                    if (!string.IsNullOrEmpty(c.Detail)) text.Append(": ").Append(c.Detail);
                    text.AppendLine();
                }

                if (Ok)
                {
                    text.AppendLine();
                    text.AppendLine("The NI drivers GPIB MCP needs are installed.");
                }
                else
                {
                    text.AppendLine();
                    foreach (string product in ProductsNeeded)
                        text.AppendLine("Install " + product + " (with .NET support): " +
                                        (product == "NI-488.2" ? Ni4882Download : NiVisaDownload));
                    if (Components.Any(c => !c.Ok && c.Product == null))
                        text.AppendLine("GPIB MCP's own files are incomplete: reinstall GPIB MCP.");
                }
                return text.ToString();
            }
        }

        public Result Run(bool startBackend = true)
        {
            var components = new List<Component>
            {
                Native("visa32.dll", "NI-VISA runtime", "NI-VISA"),
                Native("ni4882.dll", "NI-488.2 driver", "NI-488.2")
            };

            string backendPath = Path.Combine(_exeDir, BackendAssembly + ".dll");
            if (!File.Exists(backendPath))
            {
                components.Add(new Component("GPIB MCP's NI backend (" + BackendAssembly + ".dll)", null, Status.Missing,
                                             "not found in " + _exeDir));
                return new Result(components);
            }

            Assembly backend;
            try { backend = Assembly.LoadFrom(backendPath); }
            catch (Exception ex)
            {
                components.Add(new Component("GPIB MCP's NI backend (" + BackendAssembly + ".dll)", null, Status.Failed, ex.Message));
                return new Result(components);
            }

            // Everything NI the backend references, and everything NI those reference in turn: the NI-488.2
            // API needs NationalInstruments.Common, which the backend never names itself.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<AssemblyName>(NiReferences(backend));
            while (pending.Count > 0)
            {
                AssemblyName wanted = pending.Dequeue();
                if (!seen.Add(wanted.Name)) continue;

                Assembly loaded;
                components.Add(Managed(wanted, out loaded));
                if (loaded != null)
                    foreach (AssemblyName next in NiReferences(loaded)) pending.Enqueue(next);
            }

            // Last, and only when everything it needs is there: start the backend the way the server does.
            if (startBackend && components.All(c => c.Ok))
            {
                string error = _startBackend();
                components.Add(error == null
                    ? new Component("NI-VISA starts", "NI-VISA", Status.Ok, null)
                    : new Component("NI-VISA starts", "NI-VISA", Status.Failed, error));
            }

            return new Result(components);
        }

        private static IEnumerable<AssemblyName> NiReferences(Assembly assembly)
        {
            AssemblyName[] references;
            try { references = assembly.GetReferencedAssemblies(); }
            catch (Exception) { return Enumerable.Empty<AssemblyName>(); }
            return references.Where(r => r.Name.StartsWith("Ivi.", StringComparison.OrdinalIgnoreCase) ||
                                         r.Name.StartsWith("NationalInstruments.", StringComparison.OrdinalIgnoreCase));
        }

        private Component Native(string dll, string name, string product) =>
            _loadNative(dll)
                ? new Component(name + " (" + dll + ")", product, Status.Ok, null)
                : new Component(name + " (" + dll + ")", product, Status.Missing, "not found");

        private Component Managed(AssemblyName wanted, out Assembly loaded)
        {
            loaded = null;
            string product = ProductOf(wanted.Name);
            string name = FriendlyName(wanted.Name);
            try
            {
                loaded = _load(wanted);
                return new Component(name, product, Status.Ok, loaded.GetName().Version.ToString());
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is FileLoadException || ex is BadImageFormatException)
            {
                IReadOnlyList<Version> installed = _installedVersions(wanted.Name);
                if (installed.Count == 0)
                    return new Component(name, product, Status.Missing, "not installed (needs " + wanted.Version + " or later)");

                string have = string.Join(", ", installed.Select(v => v.ToString()));
                if (installed.All(v => v < wanted.Version))
                    return new Component(name, product, Status.TooOld, "needs " + wanted.Version + " or later; this PC has " + have);

                return new Component(name, product, Status.Failed, "needs " + wanted.Version + "; this PC has " + have +
                                     " but it would not load: " + ex.Message);
            }
        }

        internal static string ProductOf(string assembly)
        {
            switch (assembly)
            {
                case "Ivi.Visa":
                case "NationalInstruments.Visa":
                    return "NI-VISA";
                case "NationalInstruments.NI4882":
                case "NationalInstruments.Common":
                    return "NI-488.2";
                default:
                    return assembly.StartsWith("Ivi.", StringComparison.OrdinalIgnoreCase) ? "NI-VISA" : "NI-488.2";
            }
        }

        internal static string FriendlyName(string assembly)
        {
            switch (assembly)
            {
                case "Ivi.Visa": return "VISA.NET Shared Components (Ivi.Visa)";
                case "NationalInstruments.Visa": return "NI-VISA .NET support (NationalInstruments.Visa)";
                case "NationalInstruments.NI4882": return "NI-488.2 .NET support (NationalInstruments.NI4882)";
                case "NationalInstruments.Common": return "NI .NET common library (NationalInstruments.Common)";
                default: return assembly;
            }
        }

        // ---- the real loaders --------------------------------------------------------------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);

        private static bool LoadNative(string dll)
        {
            IntPtr module = LoadLibrary(dll);
            if (module == IntPtr.Zero) return false;
            FreeLibrary(module);
            return true;
        }

        /// <summary>The versions of an assembly in the .NET 4 GAC, for saying "too old" rather than "missing".</summary>
        private static IReadOnlyList<Version> GacVersions(string assembly)
        {
            var versions = new List<Version>();
            string gac = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "assembly");
            foreach (string kind in new[] { "GAC_MSIL", "GAC_32" })
            {
                string dir = Path.Combine(gac, kind, assembly);
                if (!Directory.Exists(dir)) continue;
                foreach (string entry in Directory.GetDirectories(dir))
                {
                    // v4.0_26.5.0.12__2eaa5af0834e221d
                    string[] parts = Path.GetFileName(entry).Split('_');
                    Version version;
                    if (parts.Length >= 2 && Version.TryParse(parts[1], out version)) versions.Add(version);
                }
            }
            return versions.Distinct().OrderBy(v => v).ToList();
        }

        private static string StartBackend()
        {
            try
            {
                using (TransportFactory.Create()) { }
                return null;
            }
            catch (Exception ex)
            {
                Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                return inner.Message;
            }
        }

        /// <summary>For messages: the version of this program.</summary>
        public static string ProgramVersion()
        {
            Assembly entry = Assembly.GetEntryAssembly() ?? typeof(NiCheck).Assembly;
            var informational = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return informational?.InformationalVersion ?? entry.GetName().Version.ToString(3);
        }
    }
}
