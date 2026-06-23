using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Reads a parameter's <c>units</c> into a <see cref="List{UnitToken}"/>, accepting all three shapes:
    /// the legacy bare string (e.g. <c>["HZ","KZ","MZ"]</c>) which becomes an UNAUDITED token (its
    /// <see cref="UnitToken.Unit"/> is null); the audited tokenful shape <c>{"token":"MZ","unit":"MHz"}</c>
    /// (a literal suffix is sent on the wire); and the audited TOKENLESS shape <c>{"unit":"V"}</c> (the
    /// instrument takes a bare number - the value's unit is recorded but no suffix goes on the bus, #46).
    /// Read-only; serialization uses the default object shape.
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
                    var unit = (string)item["unit"];
                    // {"unit":"V"} with no token = audited but tokenless (bare number on the wire).
                    if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(unit)) return null;
                    return new UnitToken(string.IsNullOrEmpty(token) ? null : token, unit);
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
