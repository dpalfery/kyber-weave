using System.Globalization;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;

namespace KyberDash.Data.Harnesses;

/// <summary>
/// Shared transcript-JSON helpers for harness mappers: text sanitizing, record-id
/// disambiguation, timestamp parsing, and the Codex payload readers duplicated across
/// transcript mappers and the transcript reader repository.
/// </summary>
public static class HarnessJson
{
    /// <summary>
    /// Returns the trimmed <paramref name="value"/>, or <see langword="null"/> when it is
    /// blank or a JSON object/array document that must be withheld from plain-text storage.
    /// </summary>
    /// <param name="value">Raw text payload to sanitize.</param>
    /// <returns>The trimmed text, or <see langword="null"/> when blank or a JSON document.</returns>
    public static string? SafeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return JsonDocumentText.IsDocument(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Returns <paramref name="candidate"/> when unused, else the first free
    /// <c><paramref name="candidate"/>-2</c>, <c>-3</c>, ... suffix.
    /// </summary>
    /// <param name="usedRecordIds">Record identifiers already emitted for this batch.</param>
    /// <param name="candidate">Preferred record identifier.</param>
    /// <returns>A record identifier not present in <paramref name="usedRecordIds"/>.</returns>
    public static string EnsureUniqueRecordId(HashSet<string> usedRecordIds, string candidate)
    {
        ArgumentNullException.ThrowIfNull(usedRecordIds);
        ArgumentNullException.ThrowIfNull(candidate);

        if (usedRecordIds.Add(candidate))
        {
            return candidate;
        }

        int suffix = 2;
        while (true)
        {
            string indexed = string.Create(CultureInfo.InvariantCulture, $"{candidate}-{suffix}");
            if (usedRecordIds.Add(indexed))
            {
                return indexed;
            }

            suffix++;
        }
    }

    /// <summary>
    /// Returns <paramref name="candidate"/> when unused, else the first free
    /// <c><paramref name="candidate"/>:<paramref name="disambiguator"/></c> or
    /// <c>...-<paramref name="disambiguator"/>-N</c> suffix.
    /// </summary>
    /// <param name="seenRecordIds">Record identifiers already emitted for this batch.</param>
    /// <param name="candidate">Preferred record identifier.</param>
    /// <param name="disambiguator">Line or step index qualifying the fallback identifier.</param>
    /// <returns>A record identifier not present in <paramref name="seenRecordIds"/>.</returns>
    public static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator)
    {
        ArgumentNullException.ThrowIfNull(seenRecordIds);
        ArgumentNullException.ThrowIfNull(candidate);

        if (seenRecordIds.Add(candidate))
        {
            return candidate;
        }

        string suffix = string.Create(CultureInfo.InvariantCulture, $"{candidate}:{disambiguator}");
        int counter = 1;
        while (!seenRecordIds.Add(suffix))
        {
            suffix = string.Create(CultureInfo.InvariantCulture, $"{candidate}:{disambiguator}-{counter++}");
        }

        return suffix;
    }

    /// <summary>
    /// Parses an ISO-8601/Roundtrip timestamp, normalizing to UTC.
    /// </summary>
    /// <param name="rawTimestamp">Raw timestamp text.</param>
    /// <returns>The parsed UTC instant, or <see langword="null"/> when blank or unparsable.</returns>
    public static DateTimeOffset? TryParseTimestamp(string? rawTimestamp)
    {
        if (!string.IsNullOrWhiteSpace(rawTimestamp)
            && DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    /// <summary>
    /// Parses an ISO-8601/Roundtrip timestamp, normalizing to UTC.
    /// </summary>
    /// <param name="timestamp">Raw timestamp text.</param>
    /// <param name="parsed">The parsed UTC instant when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="timestamp"/> parses; otherwise <see langword="false"/>.</returns>
    public static bool TryParseTimestamp(string? timestamp, out DateTimeOffset parsed)
    {
        if (!string.IsNullOrWhiteSpace(timestamp)
            && DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset instant))
        {
            parsed = instant.ToUniversalTime();
            return true;
        }

        parsed = default;
        return false;
    }

    /// <summary>
    /// Reads an optional non-negative integer or finite-double counter property.
    /// </summary>
    /// <param name="obj">JSON object carrying the property.</param>
    /// <param name="key">Property name.</param>
    /// <returns>The truncated non-negative value, or <see langword="null"/> when absent or negative.</returns>
    public static long? ReadOptionalNonNegativeLong(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (el.TryGetInt64(out long value) && value >= 0)
        {
            return value;
        }

        if (el.TryGetDouble(out double doubleVal) && double.IsFinite(doubleVal) && doubleVal >= 0)
        {
            return (long)doubleVal;
        }

        return null;
    }

    /// <summary>
    /// Reads an optional finite floating-point property.
    /// </summary>
    /// <param name="obj">JSON object carrying the property.</param>
    /// <param name="key">Property name.</param>
    /// <returns>The finite value, or <see langword="null"/> when absent or non-finite.</returns>
    public static double? ReadOptionalDouble(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (el.TryGetDouble(out double value) && double.IsFinite(value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Reads an optional string property, trimming surrounding whitespace.
    /// </summary>
    /// <param name="obj">JSON object carrying the property.</param>
    /// <param name="key">Property name.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when absent or blank.</returns>
    public static string? ReadOptionalString(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = el.GetString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Returns the first non-blank value after trimming, or <see langword="null"/>.
    /// </summary>
    /// <param name="values">Candidate values in priority order.</param>
    /// <returns>The first trimmed non-empty value, or <see langword="null"/>.</returns>
    public static string? FirstPresentString(params string?[] values)
    {
        foreach (string? value in values)
        {
            string? trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an optional 32-bit integer property.
    /// </summary>
    /// <param name="obj">JSON object carrying the property.</param>
    /// <param name="key">Property name.</param>
    /// <param name="value">The parsed value when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the property is a JSON number that fits in an <see cref="int"/>.</returns>
    public static bool TryReadInt(JsonElement obj, string key, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return el.TryGetInt32(out value);
    }

    /// <summary>
    /// Reads an optional string property, withholding blank values.
    /// </summary>
    /// <param name="obj">JSON object carrying the property.</param>
    /// <param name="key">Property name.</param>
    /// <returns>The string value, or <see langword="null"/> when absent or blank.</returns>
    public static string? ReadString(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Reads the Codex session system prompt from <c>system_prompt</c> or
    /// <c>base_instructions.text</c>.
    /// </summary>
    /// <param name="payload">Codex rollout payload object.</param>
    /// <returns>The system prompt text, or <see langword="null"/> when absent.</returns>
    public static string? ReadCodexSystemPrompt(JsonElement payload)
    {
        string? direct = ReadString(payload, "system_prompt");
        if (!string.IsNullOrEmpty(direct))
        {
            return direct;
        }

        if (payload.TryGetProperty("base_instructions", out JsonElement baseEl) && baseEl.ValueKind == JsonValueKind.Object)
        {
            return ReadString(baseEl, "text");
        }

        return null;
    }

    /// <summary>
    /// Reads the Codex <c>state.agents_md.text</c> workspace instructions.
    /// </summary>
    /// <param name="payload">Codex rollout payload object.</param>
    /// <returns>The agents-md text, or <see langword="null"/> when absent.</returns>
    public static string? ReadCodexAgentsMd(JsonElement payload)
    {
        if (!payload.TryGetProperty("state", out JsonElement stateEl) || stateEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!stateEl.TryGetProperty("agents_md", out JsonElement agentsEl) || agentsEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(agentsEl, "text");
    }

    /// <summary>
    /// Resolves the Codex harness identifier, defaulting blank or <c>codex</c> units to the
    /// canonical Codex harness id and otherwise preserving the normalized id.
    /// </summary>
    /// <param name="unitHarnessId">Harness id carried by the source unit.</param>
    /// <returns>The harness id to emit on mapped records.</returns>
    public static string ResolveCodexHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return TranscriptReaderRepository.CodexHarnessId;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
        return string.Equals(normalized, "codex", StringComparison.Ordinal)
            ? TranscriptReaderRepository.CodexHarnessId
            : normalized;
    }
    /// <summary>
    /// Returns <paramref name="candidate"/> when unused, else the first free
    /// <c><paramref name="candidate"/>:idx-1</c>, <c>:idx-2</c>, ... suffix.
    /// </summary>
    /// <param name="seenRecordIds">Record identifiers already emitted for this batch.</param>
    /// <param name="candidate">Preferred record identifier.</param>
    /// <returns>A record identifier not present in <paramref name="seenRecordIds"/>.</returns>
    public static string EnsureUniqueRecordIdWithIndexSuffix(HashSet<string> seenRecordIds, string candidate)
    {
        ArgumentNullException.ThrowIfNull(seenRecordIds);
        ArgumentNullException.ThrowIfNull(candidate);

        if (seenRecordIds.Add(candidate))
        {
            return candidate;
        }

        int suffix = 1;
        while (true)
        {
            string disambiguated = string.Create(
                CultureInfo.InvariantCulture,
                $"{candidate}:idx-{suffix.ToString(CultureInfo.InvariantCulture)}");
            if (seenRecordIds.Add(disambiguated))
            {
                return disambiguated;
            }

            suffix++;
        }
    }

    /// <summary>
    /// Reads an optional 64-bit integer property, accepting finite floating-point values
    /// by truncation.
    /// </summary>
    /// <param name="obj">JSON object carrying the property.</param>
    /// <param name="key">Property name.</param>
    /// <param name="value">The parsed value when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the property is a JSON number that fits in a <see langword="long"/>.</returns>
    public static bool TryReadLong(JsonElement obj, string key, out long value)
    {
        value = 0L;
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (el.TryGetInt64(out long i))
        {
            value = i;
            return true;
        }

        if (el.TryGetDouble(out double d) && double.IsFinite(d))
        {
            value = (long)d;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads an optional string property verbatim, without trimming or blank filtering.
    /// </summary>
    /// <param name="obj">JSON object carrying the property.</param>
    /// <param name="key">Property name.</param>
    /// <returns>The raw string value, or <see langword="null"/> when absent or not a JSON string.</returns>
    public static string? ReadStringOnly(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return el.GetString();
    }

    /// <summary>
    /// Reads an optional string property and sanitizes it via <see cref="SafeOptionalText"/>.
    /// </summary>
    /// <param name="obj">JSON object carrying the property.</param>
    /// <param name="key">Property name.</param>
    /// <returns>The trimmed text, or <see langword="null"/> when absent, blank, or a JSON document.</returns>
    public static string? ReadSafeString(JsonElement obj, string key) =>
        SafeOptionalText(ReadStringOnly(obj, key));
}
