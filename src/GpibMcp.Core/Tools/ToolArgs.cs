using System;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Shared helpers for building tool input schemas (JSON Schema) and reading typed values
    /// out of a tool's "arguments" object. Used by all tool definitions via <c>using static</c>.
    /// </summary>
    internal static class ToolArgs
    {
        // ---- JSON Schema builders -------------------------------------------

        public static JObject Schema(params JProperty[] properties)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var p in properties)
            {
                props.Add(p);
                var meta = (JObject)p.Value;
                if (meta["__required"] != null && meta["__required"].Value<bool>())
                {
                    required.Add(p.Name);
                    meta.Remove("__required");
                }
            }
            var schema = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) schema["required"] = required;
            return schema;
        }

        public static JProperty Prop(string name, string type, string description) =>
            new JProperty(name, new JObject { ["type"] = NotArray(name, type), ["description"] = description });

        public static JProperty Required(string name, string type, string description) =>
            new JProperty(name, new JObject
            {
                ["type"] = NotArray(name, type),
                ["description"] = description,
                ["__required"] = true
            });

        /// <summary>A property with a schema of its own - an <see cref="ArrayOf"/> or an <see cref="ObjectOf"/>.</summary>
        public static JProperty Prop(string name, JObject schema) => new JProperty(name, schema);

        public static JProperty Required(string name, JObject schema)
        {
            schema["__required"] = true;
            return new JProperty(name, schema);
        }

        /// <summary>
        /// An array and what each item is. Clients such as VS Code's Copilot refuse a whole tool whose schema
        /// has an array without <c>items</c> (#164), so an array can only be declared this way.
        /// </summary>
        public static JObject ArrayOf(JObject items, string description) =>
            new JObject { ["type"] = "array", ["description"] = description, ["items"] = items };

        /// <summary>An object and its properties, marked with <see cref="Prop"/> and <see cref="Required"/> as at the top level.</summary>
        public static JObject ObjectOf(string description, params JProperty[] properties)
        {
            var schema = Schema(properties);
            schema.AddFirst(new JProperty("description", description));
            return schema;
        }

        private static string NotArray(string name, string type)
        {
            if (type == "array")
                throw new ArgumentException("'" + name + "' is an array: declare it with ArrayOf, which says what its items are.");
            return type;
        }

        // ---- Argument readers -----------------------------------------------

        public static string Str(JObject args, string key, string fallback)
        {
            var token = args[key];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<string>();
        }

        public static string ReqStr(JObject args, string key)
        {
            string value = Str(args, key, null);
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("missing required argument '" + key + "'");
            return value;
        }

        public static int Int(JObject args, string key, int fallback)
        {
            var token = args[key];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<int>();
        }

        public static int Int(JObject args, string key, int fallback, int min, int max, string label)
        {
            var token = args[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                if (fallback < min) throw new ArgumentException("missing required argument '" + label + "'");
                return fallback;
            }
            int value = token.Value<int>();
            if (value < min || value > max)
                throw new ArgumentException(label + " must be between " + min + " and " + max);
            return value;
        }

        public static bool Bool(JObject args, string key, bool fallback)
        {
            var token = args[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            bool parsed;
            return bool.TryParse(token.ToString(), out parsed) ? parsed : fallback;
        }

        /// <summary>Trims trailing CR/LF that instruments append to responses.</summary>
        public static string Clean(string response) => (response ?? string.Empty).TrimEnd('\r', '\n');
    }
}
