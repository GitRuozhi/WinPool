using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinPool.Application;

// The document is already canonical JSON. Embedding it as JSON avoids escaping
// its complete contents a second time in control frames and inventory events.
public sealed class DocumentJsonStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value);
}
