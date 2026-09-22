using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desk.Connectors.Autotask;

/// <summary>
/// Autotask is not consistent about booleans: Resource.isActive and Company.isActive arrive as
/// true/false, while Contact.isActive arrives as the integer 1/0 ("Active = 1" in its own
/// documentation). A plain <c>bool</c> property throws on the integer form, and the exception
/// escaped as a 500 on every reply-recipients call for a customer that has contacts.
/// Reads any of true/false, 1/0, "1"/"0", "true"/"false"; null is false.
/// </summary>
internal sealed class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.TryGetInt64(out var n) ? n != 0 : reader.GetDouble() != 0,
            JsonTokenType.String => reader.GetString() is { } s
                && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Trim() == "1"),
            _ => false,
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
