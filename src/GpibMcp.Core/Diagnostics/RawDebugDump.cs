using System;
using System.Globalization;
using System.IO;
using System.Text;
using GpibMcp.Instruments;

namespace GpibMcp.Diagnostics
{
    /// <summary>
    /// Opt-in debug dump of verbatim raw bytes (a captured HP-GL/PCL stream, or the bytes written to a
    /// plotter) to a file under <see cref="InstrumentPaths.DebugDir"/>, written when a tool is invoked with
    /// debug=true. Lets the exact stream be inspected off-disk to diagnose plot/render glitches. Best-effort:
    /// a failure never breaks the tool call.
    /// </summary>
    public static class RawDebugDump
    {
        public static string Dir => InstrumentPaths.DebugDir();

        /// <summary>Writes <paramref name="bytes"/> verbatim to &lt;DebugDir&gt;/&lt;prefix&gt;-&lt;timestamp&gt;.&lt;ext&gt;
        /// and returns the path, or null on failure. The timestamp is injectable for tests.</summary>
        public static string Save(string prefix, string ext, byte[] bytes, string timestamp = null)
        {
            try
            {
                string dir = Dir;
                Directory.CreateDirectory(dir);
                string name = Sanitize(prefix) + "-" + (timestamp ?? Now()) + "." + (ext ?? "bin");
                string path = Path.Combine(dir, name);
                File.WriteAllBytes(path, bytes ?? Array.Empty<byte>());
                return path;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not write debug dump (" + Dir + "): " + ex.Message);
                return null;
            }
        }

        private static string Now() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder((name ?? "raw").Length);
            foreach (char c in name ?? "raw")
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
