using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Reads the join ids Claude Code's OTel events carry — <c>request_id</c> and, on
/// <c>api_response_body</c>, <c>message.id</c> (the transcript message id) — from an OTLP span's
/// or log's attributes, for the one harness that also writes a transcript file the projector
/// joins its OTLP turns to: Claude Code. Any other harness gets no ids, so the projector's id join
/// never fires for a harness with no transcript twin. Codex deliberately gets none: its OTel
/// telemetry carries no per-model-call id (<c>auth.request_id</c> names an auth request, and the
/// rollout file has no per-response id), so the projector pairs a Codex rollout model call with an
/// OTLP one one to one on equal reported counters within the twin skew instead of an id join.
/// </summary>
public static class OtlpJoinIds
{
    /// <summary>The OTLP attribute holding the transcript message id Claude Code reports on <c>api_response_body</c>.</summary>
    public const string MessageIdKey = "message.id";

    /// <summary>The OTLP attribute holding the provider request id.</summary>
    public const string RequestIdKey = "request_id";

    /// <summary>
    /// The longest id read as a join key. Claude Code's message and request ids are a few dozen
    /// characters; a longer attribute value is not one of them, so it joins nothing rather than
    /// being stored as a key or cut down to one.
    /// </summary>
    public const int MaxIdCharacters = 256;

    /// <summary>The harness id whose OTLP turns the projector joins to a transcript file twin: Claude Code.</summary>
    private static readonly HashSet<string> TwinHarnesses = new(StringComparer.Ordinal)
    {
        TranscriptReaderRepository.ClaudeHarnessId,
    };

    /// <summary>Returns the message id and request id the attributes carry, or none for a harness with no transcript twin.</summary>
    /// <param name="harnessId">The record's harness id.</param>
    /// <param name="attributes">The span's or log's attributes.</param>
    public static (string? ResponseId, string? RequestId) Read(
        string? harnessId,
        IReadOnlyDictionary<string, JsonElement>? attributes)
    {
        if (harnessId is null || !TwinHarnesses.Contains(harnessId) || attributes is null)
        {
            return (null, null);
        }

        return (ReadText(attributes, MessageIdKey), ReadText(attributes, RequestIdKey));
    }

    private static string? ReadText(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = element.GetString();
        return string.IsNullOrWhiteSpace(text) || JsonDocumentText.IsDocument(text)
            ? null
            : WithinBound(text);
    }

    /// <summary>
    /// Returns the id when it fits the join key bound, or null: an id longer than <see cref="MaxIdCharacters"/>
    /// is no join key, and is never cut down to one.
    /// </summary>
    /// <param name="id">The id to bound.</param>
    public static string? WithinBound(string? id) =>
        id is null || id.Length > MaxIdCharacters ? null : id;
}
