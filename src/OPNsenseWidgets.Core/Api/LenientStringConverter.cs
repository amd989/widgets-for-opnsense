using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OPNsenseWidgets.Api;

/// <summary>
/// Reads any JSON scalar (string, number, bool, null) into a string.
/// OPNsense changes field types between versions (e.g. KEA <c>valid_lifetime</c>
/// went from string to number), so string model fields must not crash on that.
/// </summary>
internal sealed class LenientStringConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString() ?? string.Empty;
            case JsonTokenType.Number:
                // Keep the raw text so "1730300000" round-trips exactly for long.TryParse
                return System.Text.Encoding.UTF8.GetString(
                    reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
            case JsonTokenType.True:
                return "1";
            case JsonTokenType.False:
                return "0";
            case JsonTokenType.Null:
                return string.Empty;
            default:
                // Object or array where a string was expected: skip it rather than fail the whole response
                reader.Skip();
                return string.Empty;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
