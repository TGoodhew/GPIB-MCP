using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// The context one request carries in its own <c>_meta</c> (#106, SEP-2575).
    ///
    /// MCP 2026-07-28 removes the <c>initialize</c> handshake: there is no session to remember who the
    /// client is, so every request states it - which protocol revision it speaks, what it can do, who it is.
    /// This type reads that once per request and hands it to the code that needs it, instead of each site
    /// digging through <c>_meta</c> on its own.
    ///
    /// A 2025-06-18 client sends none of it and gets an empty context: the values it declared at
    /// <c>initialize</c> still apply, so both revisions are served from one path.
    /// </summary>
    public sealed class RequestContext
    {
        /// <summary>The MCP revision this request is written in.</summary>
        public const string ProtocolVersionKey = "io.modelcontextprotocol/protocolVersion";

        /// <summary>What the client supports, including its <c>extensions</c> (e.g. tasks).</summary>
        public const string ClientCapabilitiesKey = "io.modelcontextprotocol/clientCapabilities";

        /// <summary>Who the client is - name and version. Clients SHOULD send it.</summary>
        public const string ClientInfoKey = "io.modelcontextprotocol/clientInfo";

        /// <summary>
        /// The level this request wants logged. It replaces <c>logging/setLevel</c>, and a server MUST NOT
        /// emit <c>notifications/message</c> for a request that omitted it. We emit none at all - every
        /// diagnostic goes to stderr, which is the migration the spec recommends now Logging is deprecated -
        /// so the field is recorded and nothing more.
        /// </summary>
        public const string LogLevelKey = "io.modelcontextprotocol/logLevel";

        /// <summary>Where a result names the server that produced it.</summary>
        public const string ServerInfoKey = "io.modelcontextprotocol/serverInfo";

        /// <summary>
        /// Identifies which <c>subscriptions/listen</c> request a message belongs to - its JSON-RPC id. On
        /// stdio every subscription shares one channel, so this is how a client demultiplexes them.
        /// </summary>
        public const string SubscriptionIdKey = "io.modelcontextprotocol/subscriptionId";

        /// <summary>
        /// The revision that dropped the handshake and made <c>resultType</c> required. Behaviour that only
        /// applies from there is gated on the request declaring this revision or later.
        /// </summary>
        public const string StatelessRevision = "2026-07-28";

        /// <summary>Context for a request that carried no <c>_meta</c> at all.</summary>
        public static readonly RequestContext None = new RequestContext(null);

        private readonly JObject _meta;

        public RequestContext(JObject meta)
        {
            _meta = meta;
        }

        /// <summary>The revision this request declares, or null when it declares none.</summary>
        public string ProtocolVersion => Text(ProtocolVersionKey);

        /// <summary>The capabilities this request declares, or null.</summary>
        public JObject ClientCapabilities => _meta?[ClientCapabilitiesKey] as JObject;

        /// <summary>The client identity this request declares, or null.</summary>
        public JObject ClientInfo => _meta?[ClientInfoKey] as JObject;

        /// <summary>The client's name, or null when it did not say.</summary>
        public string ClientName => ClientInfo != null ? (string)ClientInfo["name"] : null;

        /// <summary>The log level this request asks for, or null. See <see cref="LogLevelKey"/>.</summary>
        public string LogLevel => Text(LogLevelKey);

        /// <summary>The token to report progress against, or null when the client wants none.</summary>
        public JToken ProgressToken
        {
            get
            {
                JToken token = _meta?["progressToken"];
                return token == null || token.Type == JTokenType.Null ? null : token;
            }
        }

        /// <summary>
        /// True when this request declares <paramref name="revision"/> or a later one. Revisions are dated
        /// names, so ordinal comparison orders them and a future revision inherits the newer behaviour.
        /// A request that declares nothing is treated as older - the safe direction, since an old client may
        /// validate strictly against a schema that has no field for what a newer one expects.
        /// </summary>
        public bool DeclaresRevisionAtLeast(string revision)
        {
            string declared = ProtocolVersion;
            // The shape check is not pedantry: ordinal comparison would rank "latest" above every dated
            // revision, so a request declaring nonsense would be treated as newer than anything real.
            return IsRevisionName(declared) && string.CompareOrdinal(declared, revision) >= 0;
        }

        /// <summary>True for a revision name shaped like the dated ones the protocol uses (YYYY-MM-DD).</summary>
        public static bool IsRevisionName(string version)
        {
            if (version == null || version.Length != 10) return false;
            for (int i = 0; i < version.Length; i++)
            {
                bool separator = i == 4 || i == 7;
                if (separator ? version[i] != '-' : !char.IsDigit(version[i])) return false;
            }
            return true;
        }

        /// <summary>True when this request's capabilities declare <paramref name="extension"/>.</summary>
        public bool DeclaresExtension(string extension)
        {
            var extensions = ClientCapabilities?["extensions"] as JObject;
            return extensions != null && extensions[extension] != null;
        }

        /// <summary>True when the request carried nothing we recognise - i.e. a pre-2026-07-28 client.</summary>
        public bool IsEmpty => _meta == null || _meta.Count == 0;

        private string Text(string key)
        {
            JToken token = _meta?[key];
            return token == null || token.Type == JTokenType.Null ? null : (string)token;
        }
    }
}
