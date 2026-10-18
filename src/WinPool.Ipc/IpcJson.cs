using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinPool.Ipc;

public static class IpcJson
{
    public static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new RawElementConverter() }
    };

    // A document's SHA binds its original UTF-8 JSON, including escape spelling.
    // JsonElement.WriteTo re-encodes strings; every intermediate wire envelope
    // must instead retain the already validated JSON text.
    private sealed class RawElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
    }
}
