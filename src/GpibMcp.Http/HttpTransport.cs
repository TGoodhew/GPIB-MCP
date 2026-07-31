using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using GpibMcp.Diagnostics;
using GpibMcp.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Http
{
    /// <summary>
    /// MCP transport over <b>Streamable HTTP</b> (#68): a single <c>/mcp</c> endpoint served by the
    /// framework's <see cref="HttpListener"/>. A POST carries one JSON-RPC message (or a batch) and gets the
    /// response(s) back as <c>application/json</c> (202 when the POST held only notifications). This server
    /// initiates no messages, so the GET server→client SSE stream is not offered (405).
    ///
    /// For clients that can't launch a local stdio child (Microsoft Copilot, ChatGPT), typically reached via
    /// a tunnel. The module owns no protocol logic - it frames HTTP and defers to an
    /// <see cref="IMcpDispatcher"/> (which serializes, preserving the single-threaded model).
    ///
    /// Security: binds to a single host (default loopback) and validates the <c>Origin</c> header (DNS-rebinding
    /// guard). If a bearer token is configured, every request must carry <c>Authorization: Bearer &lt;token&gt;</c>;
    /// running without one is allowed only for loopback and is logged as a warning.
    /// </summary>
    public sealed class HttpTransport : IMcpTransport
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _token;
        private HttpListener _listener;

        public HttpTransport(string host, int port, string token = null)
        {
            _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            _port = port;
            _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        /// <summary>True for a host that only this machine can reach.</summary>
        private bool BindsLoopbackOnly =>
            _host == "127.0.0.1" || _host == "localhost" || _host == "::1";

        public void Run(IMcpDispatcher dispatcher)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));

            // Refuse to serve the bus to a network with no authentication at all (#114). This used to be a
            // warning, which is not a control: what is behind this endpoint is physical instrument control,
            // and the remedy is one environment variable. Loopback is still allowed without a token - that is
            // the local development case - but see the warning below about tunnels.
            if (!BindsLoopbackOnly && _token == null)
                throw new InvalidOperationException(
                    "Refusing to start: the HTTP transport is bound to " + _host + ", which is reachable from " +
                    "the network, with no bearer token. Anyone who can reach it could drive the instruments " +
                    "on the bus. Set GPIB_MCP_HTTP_TOKEN, or bind 127.0.0.1 and tunnel the port instead.");

            // No outbound sink is attached: a POST here gets exactly one JSON response and there is no
            // server→client stream, so progress notifications have nowhere to go and the dispatcher skips
            // them (#112). Task handles still work over HTTP - polling is just another POST - and restoring
            // request-scoped notifications is subscriptions/listen (#111).

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://" + _host + ":" + _port + "/");
            _listener.Start();

            Log.Info("MCP HTTP transport listening on http://" + _host + ":" + _port + "/mcp" +
                     (_token != null ? " (bearer-token auth)" : ""));
            if (_token == null)
                Log.Warn("No bearer token configured. Anyone who can reach this port can drive the " +
                         "instruments on the bus - set GPIB_MCP_HTTP_TOKEN, and note that a tunnel makes " +
                         "this port public even though it is bound to loopback.");

            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { break; }   // listener stopped/disposed
                ThreadPool.QueueUserWorkItem(_ => HandleContext(ctx, dispatcher));
            }
        }

        private void HandleContext(HttpListenerContext ctx, IMcpDispatcher dispatcher)
        {
            try
            {
                HttpListenerRequest req = ctx.Request;
                HttpListenerResponse res = ctx.Response;
                AddCors(req, res);

                // DNS-rebinding guard: a browser-originated request carries an Origin; only allow loopback ones.
                string origin = req.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
                {
                    Respond(res, 403, "text/plain", "forbidden origin"); return;
                }

                if (req.HttpMethod == "OPTIONS") { Respond(res, 204, null, null); return; }   // CORS preflight

                if (req.Url.AbsolutePath.TrimEnd('/') != "/mcp")
                {
                    Respond(res, 404, "text/plain", "not found (use /mcp)"); return;
                }

                if (_token != null && !IsAuthorized(req))
                {
                    res.AddHeader("WWW-Authenticate", "Bearer");
                    Respond(res, 401, "text/plain", "unauthorized"); return;
                }

                switch (req.HttpMethod)
                {
                    case "POST":
                        HandlePost(req, res, dispatcher);
                        break;
                    case "GET":
                    case "DELETE":
                        // Both belonged to mechanisms 2026-07-28 removed: the standalone SSE stream (now
                        // subscriptions/listen) and session teardown (there are no sessions). 405 is what the
                        // spec tells a server to answer, and it is equally right for an older client here -
                        // we have never minted a session id, so there was never anything to tear down (#110).
                        Respond(res, 405, "text/plain",
                            req.HttpMethod == "GET"
                                ? "the GET event stream is not supported; use subscriptions/listen"
                                : "sessionless: there is nothing to terminate");
                        break;
                    default:
                        Respond(res, 405, "text/plain", "method not allowed");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("HTTP handler error: " + ex.Message);
                try { Respond(ctx.Response, 500, "text/plain", "internal error"); } catch { /* response already gone */ }
            }
        }

        private void HandlePost(HttpListenerRequest req, HttpListenerResponse res, IMcpDispatcher dispatcher)
        {
            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = reader.ReadToEnd();

            JToken parsed;
            try { parsed = JToken.Parse(body); }
            catch (Exception ex)
            {
                Log.Warn("HTTP: unparseable body: " + ex.Message);
                Respond(res, 400, "application/json",
                    new JObject { ["jsonrpc"] = "2.0", ["id"] = null,
                                  ["error"] = new JObject { ["code"] = -32700, ["message"] = "Parse error" } }
                    .ToString(Formatting.None));
                return;
            }

            // A single JSON-RPC message, or a batch array. Batching left MCP in 2025-06-18 and no client we
            // serve sends one; accepting it anyway costs nothing and refusing it would help nobody. The
            // request-metadata headers describe ONE message, so they are only checked for a single body -
            // there is no meaningful Mcp-Method for an array of different methods (#110).
            bool isBatch = parsed is JArray;
            var messages = parsed is JArray arr ? arr.OfType<JObject>().ToList()
                                                : new System.Collections.Generic.List<JObject> { parsed as JObject };

            bool single = !isBatch && messages.Count == 1 && messages[0] != null;
            bool statusCodes = single && DeclaresStatelessRevision(messages[0]);

            if (single)
            {
                JObject headerError = ValidateRequestMetadata(req, messages[0]);
                if (headerError != null)
                {
                    Respond(res, 400, "application/json", headerError.ToString(Formatting.None));
                    return;
                }
            }

            var responses = new JArray();
            foreach (var m in messages)
            {
                if (m == null) continue;
                JObject r = dispatcher.Dispatch(m);
                if (r != null) responses.Add(r);
            }

            if (responses.Count == 0)
            {
                // The POST held only notifications/responses - nothing to return.
                Respond(res, 202, null, null);
                return;
            }

            JToken payload = parsed is JArray ? (JToken)responses : responses[0];
            int status = single ? StatusForResponse((JObject)responses[0], mapMethodNotFound: statusCodes) : 200;
            Respond(res, status, "application/json", payload.ToString(Formatting.None));
        }

        /// <summary>
        /// True when the request declares the revision whose transport rules map protocol errors onto HTTP
        /// status codes. Older clients keep the 200-with-a-JSON-RPC-error shape they were written against.
        /// </summary>
        private static bool DeclaresStatelessRevision(JObject message)
        {
            var prms = message["params"] as JObject;
            return new RequestContext(prms != null ? prms["_meta"] as JObject : null)
                .DeclaresRevisionAtLeast(RequestContext.StatelessRevision);
        }

        /// <summary>
        /// The HTTP status for a JSON-RPC response, per the 2026-07-28 transport rules (#110). An unknown
        /// method is <c>404</c> and an unsupported protocol version is <c>400</c> - with the JSON-RPC error
        /// still in the body, which is exactly what lets a client tell a modern server saying "I do not have
        /// that method" from a legacy server that does not host this endpoint at all. Everything else,
        /// including a tool that failed, is a perfectly good <c>200</c>: the request was served.
        ///
        /// The version refusal is <b>not</b> gated on the revision the request declared. Only a request that
        /// named a version can be refused for it, so its client is version-aware by definition - and the
        /// <c>400</c> is the signal that tells it to read the supported list and pick again. Mapping an
        /// unknown method to <c>404</c> stays gated: a legacy client may only read the body on <c>200</c>.
        /// </summary>
        private static int StatusForResponse(JObject response, bool mapMethodNotFound)
        {
            var error = response != null ? response["error"] as JObject : null;
            if (error == null) return 200;

            int code = (int?)error["code"] ?? 0;
            if (code == -32601) return mapMethodNotFound ? 404 : 200;
            if (code == McpError.UnsupportedProtocolVersionCode ||
                code == McpError.HeaderMismatchCode ||
                code == McpError.MissingRequiredClientCapabilityCode) return 400;
            return 200;
        }

        /// <summary>
        /// Checks the request-metadata headers against the body (#110, SEP-2243). The transport mirrors a few
        /// body fields into headers so an intermediary can route without parsing JSON - which only holds if
        /// the two agree. Where they disagree, a load balancer and the server would be acting on different
        /// requests, so the spec makes that <c>HeaderMismatch</c> (-32020) with a 400.
        ///
        /// Two-speed enforcement, for the same reason the rest of this revision is gated: the headers are
        /// REQUIRED from 2026-07-28, so a request declaring that revision must carry them, while a
        /// 2025-06-18 client - which is every client we serve over HTTP today - never sent them and is not
        /// asked to start. What is checked in both cases is agreement: a header that is present must be true.
        /// </summary>
        /// <returns>A JSON-RPC error response to send with 400, or null when the request is acceptable.</returns>
        internal static JObject ValidateRequestMetadata(HttpListenerRequest req, JObject message)
        {
            string method = (string)message["method"];
            if (method == null) return null;   // a response, not a request: no metadata to mirror

            var prms = message["params"] as JObject;
            var meta = prms != null ? prms["_meta"] as JObject : null;
            string declaredVersion = meta != null ? (string)meta[RequestContext.ProtocolVersionKey] : null;
            bool required = declaredVersion != null &&
                            string.CompareOrdinal(declaredVersion, RequestContext.StatelessRevision) >= 0;

            JToken id = message["id"];

            // MCP-Protocol-Version: must agree with what the body declares.
            string versionHeader = req.Headers["MCP-Protocol-Version"];
            if (versionHeader != null && declaredVersion != null && versionHeader != declaredVersion)
                return Mismatch(id, "MCP-Protocol-Version", declaredVersion, versionHeader);
            if (required && versionHeader == null)
                return Missing(id, "MCP-Protocol-Version");

            // Mcp-Method: the body's method, on every request.
            string methodHeader = req.Headers["Mcp-Method"];
            if (methodHeader != null && methodHeader != method)
                return Mismatch(id, "Mcp-Method", method, methodHeader);
            if (required && methodHeader == null)
                return Missing(id, "Mcp-Method");

            // Mcp-Name: params.name (tools/call) or params.uri (resources/read, prompts/get).
            string expectedName = prms == null ? null : ((string)prms["name"] ?? (string)prms["uri"]);
            string nameHeader = DecodeHeaderValue(req.Headers["Mcp-Name"]);
            if (nameHeader != null && expectedName != null && nameHeader != expectedName)
                return Mismatch(id, "Mcp-Name", expectedName, nameHeader);
            if (required && expectedName != null && nameHeader == null)
                return Missing(id, "Mcp-Name");

            return null;
        }

        /// <summary>
        /// Undoes the <c>=?base64?…?=</c> sentinel a client uses for a value that cannot travel as plain
        /// ASCII. Servers MUST decode before comparing, or a tool named in anything but ASCII would look
        /// like a mismatch against its own body.
        /// </summary>
        private static string DecodeHeaderValue(string value)
        {
            const string prefix = "=?base64?", suffix = "?=";
            if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
                !value.EndsWith(suffix, StringComparison.Ordinal))
                return value;

            string encoded = value.Substring(prefix.Length, value.Length - prefix.Length - suffix.Length);
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
            catch (FormatException) { return value; }   // not decodable: compare it as-is and let it mismatch
        }

        private static JObject Mismatch(JToken id, string header, string expected, string actual) =>
            ErrorResponse(id, McpError.HeaderMismatch(header, expected, actual));

        private static JObject Missing(JToken id, string header) =>
            ErrorResponse(id, new McpError(McpError.HeaderMismatchCode,
                "Missing required header '" + header + "'",
                new JObject { ["header"] = header }));

        private static JObject ErrorResponse(JToken id, McpError error)
        {
            var body = new JObject { ["code"] = error.Code, ["message"] = error.Message };
            if (error.ErrorData != null) body["data"] = error.ErrorData;
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = body
            };
        }

        private bool IsAuthorized(HttpListenerRequest req)
        {
            string auth = req.Headers["Authorization"];
            if (auth == null || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
            return FixedTimeEquals(auth.Substring("Bearer ".Length).Trim(), _token);
        }

        /// <summary>
        /// Compares two secrets without returning early on the first difference (#114). An ordinary string
        /// comparison leaks how much of the token was right through how long it took to say no; over a tunnel
        /// that is a poor attack, but a comparison that does not leak costs nothing.
        /// </summary>
        private static bool FixedTimeEquals(string presented, string expected)
        {
            if (presented == null || expected == null) return false;

            byte[] a = Encoding.UTF8.GetBytes(presented);
            byte[] b = Encoding.UTF8.GetBytes(expected);

            // The lengths themselves are not secret, but keep the loop over a fixed span either way so the
            // work done does not vary with how much of the value matched.
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i % Math.Max(b.Length, 1)];
            return diff == 0;
        }

        private static bool IsLoopbackOrigin(string origin)
        {
            return Uri.TryCreate(origin, UriKind.Absolute, out var u) &&
                   (u.IsLoopback || u.Host == "localhost");
        }

        private static void AddCors(HttpListenerRequest req, HttpListenerResponse res)
        {
            string origin = req.Headers["Origin"];
            res.AddHeader("Access-Control-Allow-Origin", string.IsNullOrEmpty(origin) ? "*" : origin);
            res.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
            // Mcp-Session-Id is gone with sessions; Mcp-Method/Mcp-Name are the request metadata a
            // 2026-07-28 client mirrors from the body, so a browser-origin client must be allowed to send
            // them (#110).
            res.AddHeader("Access-Control-Allow-Headers",
                "Content-Type, Authorization, MCP-Protocol-Version, Mcp-Method, Mcp-Name, Accept");
        }

        private static void Respond(HttpListenerResponse res, int status, string contentType, string body)
        {
            try
            {
                res.StatusCode = status;
                if (body == null)
                {
                    res.ContentLength64 = 0;
                }
                else
                {
                    res.ContentType = contentType;
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                }
            }
            finally { res.OutputStream.Close(); }
        }

        public void Dispose()
        {
            try { _listener?.Stop(); } catch { /* best effort */ }
            try { _listener?.Close(); } catch { /* best effort */ }
        }
    }
}
