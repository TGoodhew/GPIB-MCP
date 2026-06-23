using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Reads a parameter's <c>units</c> into a <see cref="List{UnitToken}"/>, accepting BOTH the legacy
    /// shape (a string or array of strings, e.g. <c>["HZ","KZ","MZ"]</c>) and the audited shape (objects,
    /// e.g. <c>[{"token":"MZ","unit":"MHz"}]</c>). A bare string becomes an unaudited token (its
    /// <see cref="UnitToken.Unit"/> is null) so every existing definition keeps parsing while #46 migrates
    /// them family by family. Read-only; serialization uses the default object shape.
    /// </summary>
    internal sealed class UnitTokenListConverter : JsonConverter
    {
        public override bool CanWrite => false;
        public override bool CanConvert(Type objectType) => objectType == typeof(List<UnitToken>);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                                        JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            switch (token.Type)
            {
                case JTokenType.Null:
                    return null;
                case JTokenType.Array:
                    var list = new List<UnitToken>();
                    foreach (var item in (JArray)token)
                    {
                        UnitToken ut = ToUnitToken(item);
                        if (ut != null) list.Add(ut);
                    }
                    return list;
                default:
                    var one = ToUnitToken(token);
                    return one != null ? new List<UnitToken> { one } : new List<UnitToken>();
            }
        }

        private static UnitToken ToUnitToken(JToken item)
        {
            switch (item.Type)
            {
                case JTokenType.Null:
                    return null;
                case JTokenType.Object:
                    var token = (string)item["token"];
                    if (string.IsNullOrEmpty(token)) return null;
                    return new UnitToken(token, (string)item["unit"]);
                default:
                    // Legacy bare string: a token whose physical meaning is not yet audited.
                    string s = item.ToString();
                    return string.IsNullOrEmpty(s) ? null : new UnitToken(s);
            }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
            throw new NotSupportedException();
    }
}
