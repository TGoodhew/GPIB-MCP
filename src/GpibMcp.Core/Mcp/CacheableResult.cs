using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// The caching hints MCP 2026-07-28 puts on results a client may reuse instead of re-fetching
    /// (SEP-2549): <c>ttlMs</c>, how long the answer stays fresh, and <c>cacheScope</c>, who may hold it.
    ///
    /// They apply to the list-shaped methods - here, <c>server/discover</c> and <c>tools/list</c>. Both
    /// answers are fixed for the life of the process: the tool registry is built once at start-up and the
    /// capability set with it, so nothing either returns can change without a restart. That makes a generous
    /// TTL not just safe but worth having - 29 tool descriptors is a lot of prompt to re-send.
    /// </summary>
    internal static class CacheableResult
    {
        /// <summary>
        /// One hour. Bounded rather than infinite so a client that outlives a server restart re-reads
        /// eventually without being told to; the <c>listChanged</c> capability stays false either way.
        /// </summary>
        public const int DefaultTtlMs = 60 * 60 * 1000;

        /// <summary>
        /// <c>"private"</c> - a shared intermediary must not cache these. The scope exists for servers with
        /// many users and one answer; this one has a single user by construction (one bench, one bus) and
        /// its answers describe that person's instruments. Public caching would buy nothing here, and the
        /// case where it could apply at all - the HTTP transport behind a tunnel - is precisely the case
        /// where an intermediary holding the response is unwanted.
        /// </summary>
        public const string DefaultCacheScope = "private";

        /// <summary>Stamps the freshness hints onto <paramref name="result"/> and returns it.</summary>
        public static JObject ApplyTo(JObject result, int ttlMs = DefaultTtlMs,
                                      string cacheScope = DefaultCacheScope)
        {
            if (result == null) return null;
            result["ttlMs"] = ttlMs;
            result["cacheScope"] = cacheScope;
            return result;
        }
    }
}
