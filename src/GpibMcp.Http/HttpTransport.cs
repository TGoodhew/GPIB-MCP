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

        public void Run(IMcpDispatcher dispatcher)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://" + _host + ":" + _port + "/");
            _listener.Start();

            bool loopback = _host == "127.0.0.1" || _host == "localhost" || _host == "::1";
            Log.Info("MCP HTTP transport listening on http://" + _host + ":" + _port + "/mcp" +
                     (_token != null ? " (bearer-token auth)" : ""));
            if (_token == null && !loopback)
                Log.Warn("HTTP transport is bound to a non-loopback host with NO token - set GPIB_MCP_HTTP_TOKEN.");

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
                        // No server-initiated messages, so no SSE stream to open.
                        Respond(res, 405, "text/plain", "the GET event stream is not supported");
                        break;
                    case "DELETE":
                        // Sessionless: nothing to terminate.
                        Respond(res, 200, "text/plain", "ok");
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

            // A single JSON-RPC message or a batch array.
            var messages = parsed is JArray arr ? arr.OfType<JObject>().ToList()
                                                : new System.Collections.Generic.List<JObject> { parsed as JObject };
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
            Respond(res, 200, "application/json", payload.ToString(Formatting.None));
        }

        private bool IsAuthorized(HttpListenerRequest req)
        {
            string auth = req.Headers["Authorization"];
            return auth != null &&
                   auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(auth.Substring("Bearer ".Length).Trim(), _token, StringComparison.Ordinal);
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
            res.AddHeader("Access-Control-Allow-Methods", "POST, GET, DELETE, OPTIONS");
            res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization, Mcp-Session-Id, MCP-Protocol-Version, Accept");
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
