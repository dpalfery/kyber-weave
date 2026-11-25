using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Reads the provider response id (<c>gen_ai.response.id</c>) and request id (<c>request_id</c>)
/// from an OTLP span's or log's attributes, for the harnesses that also write a transcript file
/// the projector joins their OTLP turns to: Claude Code and Codex. Any other harness gets no ids,
/// so the projector's id join never fires for a harness with no transcript twin.
/// </summary>
internal static class OtlpJoinIds
{
    /// <summary>The OTLP attribute holding the provider response id.</summary>
    internal const string ResponseIdKey = "gen_ai.response.id";

    /// <summary>The OTLP attribute holding the provider request id.</summary>
    internal const string RequestIdKey = "request_id";

    /// <summary>
    /// The harness ids Claude Code and Codex records carry: the log mapper names Codex
    /// <c>codex</c>, the span mapper <c>codex-cli</c>.
    /// </summary>
    private static readonly HashSet<string> TwinHarnesses = new(StringComparer.Ordinal)
    {
        TranscriptReaderRepository.ClaudeHarnessId,
        TranscriptReaderRepository.CodexHarnessId,
        "codex",
    };

    /// <summary>Returns the response id and request id the attributes carry, or none for a harness with no transcript twin.</summary>
    /// <param name="harnessId">The record's harness id.</param>
    /// <param name="attributes">The span's or log's attributes.</param>
    internal static (string? ResponseId, string? RequestId) Read(
        string? harnessId,
        IReadOnlyDictionary<string, JsonElement>? attributes)
    {
        if (harnessId is null || !TwinHarnesses.Contains(harnessId) || attributes is null)
        {
            return (null, null);
        }

        return (ReadText(attributes, ResponseIdKey), ReadText(attributes, RequestIdKey));
    }

    private static string? ReadText(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = element.GetString();
        return string.IsNullOrWhiteSpace(text) || JsonDocumentText.IsDocument(text) ? null : text;
    }
}
