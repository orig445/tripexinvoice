using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripEx.Api.Json;

/// <summary>
/// Reads a JSON string OR number into a C# string property. TAS's own widget sends
/// "trid" as a bare JSON number (e.g. trid:0) rather than a string, and System.Text.Json
/// rejects that mismatch with a JsonException -> ASP.NET Core turns it into an automatic
/// 400 Bad Request before the request ever reaches the controller. TAS's request shape is
/// fixed and out of our control, so this side accepts either representation.
/// </summary>
public class FlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to string"),
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
