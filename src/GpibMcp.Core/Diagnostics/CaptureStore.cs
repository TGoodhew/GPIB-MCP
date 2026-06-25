using System;
using System.Globalization;
using System.IO;
using System.Text;
using GpibMcp.Instruments;

namespace GpibMcp.Diagnostics
{
    /// <summary>
    /// Server-side retention of a screen capture's verbatim forwardable bytes (#79). Every plot/print
    /// capture writes its exact bytes to a file under <see cref="InstrumentPaths.CapturesDir"/> and returns
    /// the path as a small handle. Forwarding a plot to a plotter then passes only that handle to
    /// <c>visa_write_raw(path=...)</c>, so the payload never round-trips through the model as base64 (the
    /// dominant forwarding delay this issue removes). The store keeps only the most recent
    /// <see cref="KeepLast"/> files so captures don't accumulate. Best-effort: a failure never breaks the
    /// capture - it just means send-by-reference isn't available for that one.
    /// </summary>
    public static class CaptureStore
    {
        public static string Dir => InstrumentPaths.CapturesDir();

        /// <summary>How many capture files to retain; older ones are pruned on each save.</summary>
        public const int KeepLast = 50;

        /// <summary>Writes <paramref name="bytes"/> verbatim to &lt;CapturesDir&gt;/capture-&lt;model&gt;-&lt;timestamp&gt;.&lt;ext&gt;,
        /// prunes the directory to the most recent <see cref="KeepLast"/> files, and returns the path (or null
        /// on failure). The timestamp is injectable for tests.</summary>
        public static string Save(string model, string ext, byte[] bytes, string timestamp = null)
        {
            try
            {
                string dir = Dir;
                Directory.CreateDirectory(dir);
                string name = "capture-" + Sanitize(model) + "-" + (timestamp ?? Now()) + "." + (ext ?? "bin");
                string path = Path.Combine(dir, name);
                File.WriteAllBytes(path, bytes ?? Array.Empty<byte>());
                Prune(dir, KeepLast);
                return path;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not retain capture (" + Dir + "): " + ex.Message);
                return null;
            }
        }

        private static void Prune(string dir, int keep)
        {
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("capture-*.*");
                if (files.Length <= keep) return;
                // Newest first: by write time, tie-broken by the sortable timestamp in the name (rapid saves
                // can share a filesystem time tick, so the name keeps the ordering deterministic).
                Array.Sort(files, (a, b) =>
                {
                    int c = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
                    return c != 0 ? c : string.CompareOrdinal(b.Name, a.Name);
                });
                for (int i = keep; i < files.Length; i++)
                {
                    try { files[i].Delete(); }
                    catch (Exception ex) { Log.Warn("Could not prune capture " + files[i].Name + ": " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not prune captures (" + dir + "): " + ex.Message);
            }
        }

        private static string Now() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder((name ?? "capture").Length);
            foreach (char c in name ?? "capture")
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
