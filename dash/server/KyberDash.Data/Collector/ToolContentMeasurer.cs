using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;

namespace KyberDash.Data.Collector;

/// <summary>
/// Measurement and classification result produced by <see cref="ToolContentMeasurer"/>.
/// Plain text is withheld (<see langword="null"/>) whenever the input is a JSON document.
/// </summary>
/// <param name="PlainText">Preserved plain text (truncated if oversized), or <see langword="null"/> when withheld as JSON.</param>
/// <param name="Utf8Bytes">Original UTF-8 byte count before any truncation or withholding.</param>
/// <param name="Characters">Original UTF-16 character count before any truncation or withholding.</param>
/// <param name="MeasuredTokens">Token count the local tokenizer gives the original text under the record's model (decision D17).</param>
/// <param name="Sha256Fingerprint">64-character lowercase hex SHA-256 over canonical sorted-key minified JSON or plain text.</param>
/// <param name="OriginalUtf8BytesWhenTruncated">Original UTF-8 byte length when <see cref="PlainText"/> was truncated; otherwise <see langword="null"/>.</param>
/// <param name="IsDocument"><see langword="true"/> when the input was classified as a JSON object/array (including nested JSON strings).</param>
/// <param name="MeasuredTokenizer">Name of the tokenizer that produced <paramref name="MeasuredTokens"/>, such as <c>o200k_base</c>.</param>
/// <param name="MeasuredTokenizerIsModelOwn"><see langword="true"/> when <paramref name="MeasuredTokenizer"/> is the model's own tokenizer.</param>
public record ToolContentMeasurement(
    string? PlainText,
    long Utf8Bytes,
    long Characters,
    long MeasuredTokens,
    string Sha256Fingerprint,
    long? OriginalUtf8BytesWhenTruncated,
    bool IsDocument,
    string MeasuredTokenizer,
    bool MeasuredTokenizerIsModelOwn = false)
{
    /// <summary>Projects this measurement into a <see cref="CollectedToolContent"/> DTO.</summary>
    /// <param name="includeFingerprint">Whether to retain <see cref="Sha256Fingerprint"/> (typically true for tool inputs).</param>
    /// <returns>The projected <see cref="CollectedToolContent"/>.</returns>
    public CollectedToolContent ToToolContent(bool includeFingerprint = true) =>
        new(
            PlainText: PlainText,
            Utf8Bytes: Utf8Bytes,
            Characters: Characters,
            Sha256Fingerprint: includeFingerprint ? Sha256Fingerprint : null,
            OriginalUtf8BytesWhenTruncated: OriginalUtf8BytesWhenTruncated);

    /// <summary>Projects this measurement into a <see cref="CollectedWithheldText"/> DTO.</summary>
    /// <returns>The projected <see cref="CollectedWithheldText"/>.</returns>
    public CollectedWithheldText ToWithheldText() =>
        new(
            Utf8Bytes: Utf8Bytes,
            Characters: Characters,
            MeasuredTokens: MeasuredTokens,
            Tokenizer: MeasuredTokenizer,
            TokenizerIsModelOwn: MeasuredTokenizerIsModelOwn);
}

/// <summary>
/// Measures tool inputs, tool results, and content parts while withholding JSON documents
/// (including nested JSON strings that parse as JSON objects or arrays) and bounding stored
/// plain text to <see cref="CanonSynth.MaxToolResultBytes"/>.
/// </summary>
public static class ToolContentMeasurer
{
    private const int MaxNestedJsonStringDepth = 8;

    private static readonly JsonWriterOptions CanonicalWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Measures <paramref name="text"/>, withholding <see cref="ToolContentMeasurement.PlainText"/>
    /// when it parses as a JSON object or array (directly or via nested JSON string unwrapping)
    /// and truncating preserved plain text above <paramref name="maxPlainTextBytes"/>.
    /// </summary>
    /// <remarks>
    /// Counts with the shared local tokenizer (<see cref="LocalTextTokenizer.Shared"/>) under
    /// <paramref name="model"/>; a blank model counts under the labelled stand-in (D19).
    /// </remarks>
    /// <param name="text">Raw content string to classify and measure.</param>
    /// <param name="maxPlainTextBytes">Maximum UTF-8 bytes preserved when keeping plain text.</param>
    /// <param name="model">Model the record carrying the text was sent to; <see langword="null"/> when unknown.</param>
    /// <returns>The <see cref="ToolContentMeasurement"/>.</returns>
    public static ToolContentMeasurement Measure(
        string text,
        int maxPlainTextBytes = CanonSynth.MaxToolResultBytes,
        string? model = null) =>
        Measure(text, LocalTextTokenizer.Shared, model, maxPlainTextBytes);

    /// <summary>
    /// Measures <paramref name="text"/> as <see cref="Measure(string, int, string?)"/> does, counting
    /// with <paramref name="tokenizer"/>.
    /// </summary>
    /// <param name="text">Raw content string to classify and measure.</param>
    /// <param name="tokenizer">Tokenizer that counts the text and names itself on the result.</param>
    /// <param name="model">Model the record carrying the text was sent to; <see langword="null"/> when unknown.</param>
    /// <param name="maxPlainTextBytes">Maximum UTF-8 bytes preserved when keeping plain text.</param>
    /// <returns>The <see cref="ToolContentMeasurement"/>.</returns>
    public static ToolContentMeasurement Measure(
        string text,
        ITextTokenizer tokenizer,
        string? model,
        int maxPlainTextBytes = CanonSynth.MaxToolResultBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPlainTextBytes, 0);

        long utf8Bytes = Encoding.UTF8.GetByteCount(text);
        long characters = text.Length;
        TokenCount measured = tokenizer.Count(text, model);
        long measuredTokens = measured.Count;

        if (TryParseDocumentOrNested(text, out JsonDocument? document))
        {
            using (document)
            {
                string sha256 = HashCanonicalJson(document.RootElement);
                return new ToolContentMeasurement(
                    PlainText: null,
                    Utf8Bytes: utf8Bytes,
                    Characters: characters,
                    MeasuredTokens: measuredTokens,
                    Sha256Fingerprint: sha256,
                    OriginalUtf8BytesWhenTruncated: null,
                    IsDocument: true,
                    MeasuredTokenizer: measured.Tokenizer,
                    MeasuredTokenizerIsModelOwn: measured.IsModelOwn);
            }
        }

        string plainSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        bool truncated = utf8Bytes > maxPlainTextBytes;
        string plainText = truncated ? CanonValidation.TruncateContentPartUtf8(text, maxPlainTextBytes) : text;
        long? originalBytes = truncated ? utf8Bytes : null;

        return new ToolContentMeasurement(
            PlainText: plainText,
            Utf8Bytes: utf8Bytes,
            Characters: characters,
            MeasuredTokens: measuredTokens,
            Sha256Fingerprint: plainSha256,
            OriginalUtf8BytesWhenTruncated: originalBytes,
            IsDocument: false,
            MeasuredTokenizer: measured.Tokenizer,
            MeasuredTokenizerIsModelOwn: measured.IsModelOwn);
    }

    /// <summary>
    /// Measures a <see cref="JsonElement"/> payload by unwrapping string elements or serializing
    /// structured elements before applying <see cref="Measure(string, int, string?)"/>.
    /// </summary>
    /// <param name="element">JSON element to classify and measure.</param>
    /// <param name="maxPlainTextBytes">Maximum UTF-8 bytes preserved when keeping plain text.</param>
    /// <param name="model">Model the record carrying the element was sent to; <see langword="null"/> when unknown.</param>
    /// <returns>The <see cref="ToolContentMeasurement"/>.</returns>
    public static ToolContentMeasurement Measure(
        JsonElement element,
        int maxPlainTextBytes = CanonSynth.MaxToolResultBytes,
        string? model = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPlainTextBytes, 0);

        string raw = element.ValueKind switch
        {
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => element.GetRawText(),
        };

        return Measure(raw, maxPlainTextBytes, model);
    }

    /// <summary>
    /// Measures a raw tool-result or message text at <see cref="CanonSynth.MaxToolResultBytes"/>
    /// and projects it into the canonical <see cref="CollectedContentPart"/>: document-classified
    /// text is withheld in full, and truncated plain text reports its original UTF-8 byte length.
    /// Every harness mapper builds its content parts through this helper so the canonical part
    /// contract stays identical across harnesses.
    /// </summary>
    /// <param name="position">Content-part position within the record.</param>
    /// <param name="bucket">Content bucket the part belongs to.</param>
    /// <param name="rawText">Raw text to classify and measure.</param>
    /// <param name="role">Speaker role carried on the part.</param>
    /// <param name="model">Model the part's record was sent to; <see langword="null"/> when unknown.</param>
    /// <returns>The projected <see cref="CollectedContentPart"/>.</returns>
    public static CollectedContentPart BuildContentPart(int position, ContentBucket bucket, string rawText, string? role, string? model = null)
    {
        ToolContentMeasurement measurement = Measure(rawText, CanonSynth.MaxToolResultBytes, model);
        return new CollectedContentPart(
            Position: position,
            Bucket: bucket,
            PlainText: measurement.IsDocument ? null : measurement.PlainText,
            Withheld: measurement.IsDocument ? measurement.ToWithheldText() : null,
            ReportedTokens: null,
            Server: null,
            Role: role,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: measurement.IsDocument
                ? null
                : measurement.OriginalUtf8BytesWhenTruncated);
    }

    private static bool TryParseDocumentOrNested(string text, [NotNullWhen(true)] out JsonDocument? document)
    {
        string current = text;
        for (int depth = 0; depth <= MaxNestedJsonStringDepth; depth++)
        {
            if (!TryParseTrimmedCandidate(current, out JsonDocument? parsed))
            {
                document = null;
                return false;
            }

            if (parsed.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                document = parsed;
                return true;
            }

            if (parsed.RootElement.ValueKind == JsonValueKind.String)
            {
                string? unwrapped = parsed.RootElement.GetString();
                parsed.Dispose();
                if (string.IsNullOrWhiteSpace(unwrapped))
                {
                    document = null;
                    return false;
                }

                current = unwrapped;
                continue;
            }

            parsed.Dispose();
            document = null;
            return false;
        }

        document = null;
        return false;
    }

    private static bool TryParseTrimmedCandidate(string text, [NotNullWhen(true)] out JsonDocument? document)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            document = null;
            return false;
        }

        int start = 0;
        int end = text.Length - 1;
        while (start <= end && char.IsWhiteSpace(text[start]))
            start++;
        while (end >= start && char.IsWhiteSpace(text[end]))
            end--;

        if (start > end)
        {
            document = null;
            return false;
        }

        char first = text[start];
        if (first is not ('{' or '[' or '"'))
        {
            document = null;
            return false;
        }

        try
        {
            ReadOnlyMemory<char> trimmed = text.AsMemory(start, end - start + 1);
            document = JsonDocument.Parse(trimmed);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static string HashCanonicalJson(JsonElement root)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, CanonicalWriterOptions))
        {
            WriteCanonicalJson(writer, root);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(static prop => prop.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
