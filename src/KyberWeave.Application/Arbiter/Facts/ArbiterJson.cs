using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>Serializer settings shared by both append-only files.</summary>
internal static class ArbiterJson
{
    /// <summary>
    /// Null optional fields are omitted rather than written: a record's
    /// observable field set is exactly the fields it populated, which keeps
    /// both files free of fields the design does not define (Req 18.4).
    /// </summary>
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new ArbiterUtcTimestampConverter());
        return options;
    }

    /// <summary>Serializes a record to one compact JSON line.</summary>
    internal static string Serialize<T>(T record)
        where T : notnull =>
        JsonSerializer.Serialize(record, Options);
}

/// <summary>
/// Writes timestamps as UTC in the round-trip <c>o</c> format with a <c>Z</c>
/// suffix, so the ledger's <c>at</c> field is unambiguously UTC; the default
/// serializer would spell a zero offset <c>+00:00</c>.
/// </summary>
internal sealed class ArbiterUtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(
            reader.GetString() ?? throw new JsonException("expected a timestamp string"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
}
