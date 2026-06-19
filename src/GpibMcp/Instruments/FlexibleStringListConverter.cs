using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Reads a JSON value that may be EITHER a single string OR an array of strings into a
    /// <see cref="List{String}"/>. This makes hand-authored and machine-extracted definitions
    /// forgiving: a field like <c>"units": "V"</c> is accepted just like <c>"units": ["V"]</c>,
    /// so a single malformed field never causes a whole instrument file to be dropped.
    ///
    /// Read-only: serialization uses the default (always an array), keeping output canonical.
    /// </summary>
    internal sealed class FlexibleStringListConverter : JsonConverter
    {
        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType) => objectType == typeof(List<string>);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                                        JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            switch (token.Type)
            {
                case JTokenType.Null:
                    return null;
                case JTokenType.Array:
                    var list = new List<string>();
                    foreach (var item in (JArray)token)
                        if (item.Type != JTokenType.Null) list.Add(item.ToString());
                    return list;
                default:
                    return new List<string> { token.ToString() };
            }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
            throw new NotSupportedException();
    }
}
