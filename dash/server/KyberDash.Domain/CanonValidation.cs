using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberDash.Domain;

/// <summary>
/// Canonical content bounding, part collapsing, and token decomposition validation
/// (<c>dash/src/canon/types.ts</c> and <c>dash/src/canon/ingest.ts</c>).
/// </summary>
public static class CanonValidation
{
    /// <summary>Maximum UTF-8 byte length stored for a single collapsed content part (64 KiB).</summary>
    public const int MaxContentPartBytes = 64 * 1024;

    /// <summary>Maximum UTF-16 character length stored for a quarantine bounded quarantine evidence (256 Ki chars).</summary>
    public const int MaxQuarantineRawPayloadChars = 256 * 1024;

    /// <summary>Problem code emitted when <c>freshInput &lt; 0</c> after convention conversion.</summary>
    public const string TokenNegativeFresh = "TOKEN_NEGATIVE_FRESH";

    /// <summary>Problem code emitted when any cache or output token class is negative.</summary>
    public const string TokenNegativeClass = "TOKEN_NEGATIVE_CLASS";

    /// <summary>Problem code emitted when <c>freshInput + cacheRead + cacheCreation != reportedInput</c>.</summary>
    public const string TokenSumMismatch = "TOKEN_SUM_MISMATCH";

    /// <summary>Problem code emitted when reasoning tokens are negative or exceed measured output tokens.</summary>
    public const string TokenReasoningExceedsOutput = "TOKEN_REASONING_EXCEEDS_OUTPUT";

    /// <summary>
    /// All canonical context content bucket keys recognized downstream.
    /// </summary>
    public static IReadOnlyList<string> CanonicalContentKeys { get; } =
    [
        "system_prompt",
        "tool_definitions",
        "instruction_context",
        "conversation_history",
        "tool_result_content",
    ];

    /// <summary>
    /// Truncates <paramref name="text"/> so its UTF-8 encoding fits within
    /// <paramref name="maxBytes"/> bytes without splitting multi-byte UTF-8 sequences or UTF-16
    /// surrogate pairs.
    /// </summary>
    public static string TruncateContentPartUtf8(string text, int maxBytes = MaxContentPartBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxBytes <= 0 || text.Length == 0)
            return string.Empty;

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        int byteCount = 0;
        int charCount = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes)
                break;

            byteCount += runeBytes;
            charCount += rune.Utf16SequenceLength;
        }

        // The rune walk above already bounds the prefix to maxBytes, so no rescan is needed here.
        // Rescanning per character made truncation quadratic in the length of attacker content.
        if (charCount > 0 && char.IsHighSurrogate(text[charCount - 1]))
            charCount--;

        return charCount == 0 ? string.Empty : text[..charCount];
    }

    /// <summary>
    /// Collapses structured <paramref name="parts"/> into the flat canonical content map,
    /// stably ordered by <see cref="ContentPart.Order"/> and bounded per bucket to
    /// <see cref="MaxContentPartBytes"/> UTF-8 bytes.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ContentFromParts(IEnumerable<ContentPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        Dictionary<string, List<string>> grouped = new(StringComparer.Ordinal);
        foreach (ContentPart part in parts.OrderBy(static item => item.Order ?? 0))
        {
            if (string.IsNullOrEmpty(part.Text))
                continue;

            if (!grouped.TryGetValue(part.Part, out List<string>? texts))
            {
                texts = [];
                grouped.Add(part.Part, texts);
            }

            texts.Add(part.Text);
        }

        Dictionary<string, string> content = new(grouped.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<string>> entry in grouped)
        {
            content[entry.Key] = TruncateContentPartUtf8(string.Join('\n', entry.Value));
        }

        return content;
    }

    /// <summary>
    /// Bounds a serialized decoded-span JSON payload for <c>quarantine</c> evidence, appending a
    /// truncation marker when <paramref name="json"/> exceeds <see cref="MaxQuarantineRawPayloadChars"/>.
    /// </summary>
    public static string BoundRawPayload(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length <= MaxQuarantineRawPayloadChars)
            return json;

        int cut = MaxQuarantineRawPayloadChars;
        if (cut > 0 && char.IsHighSurrogate(json[cut - 1]))
            cut--;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{json[..cut]}\n...[truncated: stored {cut} of {json.Length} characters]");
    }

    /// <summary>
    /// Validates the disjoint token decomposition invariant on <paramref name="tokens"/>, returning
    /// the blocking <see cref="Problem"/> or <see langword="null"/> when valid.
    /// </summary>
    public static Problem? ValidateTokens(
        TokenUsage tokens,
        string? location = null,
        IReadOnlyDictionary<string, JsonElement>? measurability = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return ValidateTokens(tokens, location, IsOutputUnmeasured(measurability));
    }

    /// <summary>
    /// Validates the disjoint token decomposition invariant on <paramref name="tokens"/>, returning
    /// the blocking <see cref="Problem"/> or <see langword="null"/> when valid.
    /// </summary>
    public static Problem? ValidateTokens(
        TokenUsage tokens,
        string? location,
        bool outputUnmeasured)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (tokens.FreshInput < 0)
        {
            return new Problem(
                ProblemSeverity.Error,
                TokenNegativeFresh,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"fresh input is negative ({tokens.FreshInput}); the harness's input convention was likely inverted on the way in"),
                location);
        }

        if (tokens.CacheRead < 0 || tokens.CacheCreation < 0 || tokens.Output < 0)
        {
            return new Problem(
                ProblemSeverity.Error,
                TokenNegativeClass,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"token class is negative (cacheRead={tokens.CacheRead}, cacheCreation={tokens.CacheCreation}, output={tokens.Output})"),
                location);
        }

        if (tokens.Reasoning.HasValue && (tokens.Reasoning.Value < 0 || (!outputUnmeasured && tokens.Reasoning.Value > tokens.Output)))
        {
            return new Problem(
                ProblemSeverity.Error,
                TokenReasoningExceedsOutput,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"reasoning ({tokens.Reasoning.Value}) is not a subset of output ({tokens.Output})"),
                location);
        }

        // Every class is non-negative by the guards above, so the sum of three longs can exceed
        // long.MaxValue. Sum in decimal, which holds it exactly and cannot wrap.
        decimal inputSum = (decimal)tokens.FreshInput + tokens.CacheRead + tokens.CacheCreation;
        if (inputSum != tokens.ReportedInput)
        {
            return new Problem(
                ProblemSeverity.Error,
                TokenSumMismatch,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"fresh_input + cache_read + cache_creation ({inputSum}) does not reconcile with reported_input ({tokens.ReportedInput})"),
                location);
        }

        return null;
    }

    private static bool IsOutputUnmeasured(IReadOnlyDictionary<string, JsonElement>? measurability)
    {
        if (measurability is null)
            return false;

        if (!measurability.TryGetValue("output", out JsonElement availability)
            && !measurability.TryGetValue("output_tokens", out availability))
        {
            return false;
        }

        return availability.ValueKind == JsonValueKind.Object
            && availability.TryGetProperty("availability", out JsonElement status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(status.GetString(), "not_measurable", StringComparison.Ordinal);
    }
}
