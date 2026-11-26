using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// The one record-hydration rule that lifts a relational <see cref="CollectedRecord"/> into the
/// <see cref="CanonicalRecord"/> shape the shared analysis pipeline reads.
/// </summary>
/// <remarks>
/// Both read paths that hydrate out of the relational store — session projection
/// (<see cref="CanonSessionProjectorEngine"/>) and the bridge's session analytics
/// (<see cref="KyberBridgeEngine"/>) — call <see cref="Hydrate"/>, so the two can never disagree
/// about op, kind, status, timestamp, part ordering, tool-definition placement, cost basis, or
/// the raw attribute bag.
/// </remarks>
public static class CanonRecordHydration
{
    /// <summary>
    /// Source label for a record that carries no <see cref="CollectedRecord.SourceName"/>: one a
    /// mapper built in memory and that never went through a store read.
    /// </summary>
    private const string RelationalSourceName = "relational";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Order base placing typed tool definitions after captured content parts.</summary>
    private const int TypedDefinitionOrderBase = 1_000;

    /// <summary>Agent-instance marker emitted on the first kept turn after a Claude compaction boundary.</summary>
    internal const string CompactBoundaryAgentInstanceId = "claude:compact_boundary";

    /// <summary>Canonical raw attribute key surfacing <see cref="CompactBoundaryAgentInstanceId"/> for projection reset.</summary>
    internal const string CompactBoundaryRawKey = "claude.compact_boundary";

    /// <summary>
    /// Hydrates one relational <see cref="CollectedRecord"/> into the canonical analysis record:
    /// typed token, part, tool-definition, agent, and declared-window columns become the token
    /// usage, content parts, and minimal raw attributes the analysis pipeline reads.
    /// </summary>
    /// <param name="record">Relational record read through <see cref="IRelationalStoreReader"/>.</param>
    /// <returns>Hydrated canonical record for the shared analysis pipeline.</returns>
    public static CanonicalRecord Hydrate(CollectedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Dictionary<string, JsonElement> raw = new(StringComparer.Ordinal);
        if (record.Signals.DeclaredContextWindow is > 0)
        {
            raw[CanonPricing.DeclaredContextLimitKey] = JsonSerializer.SerializeToElement(record.Signals.DeclaredContextWindow.Value);
        }

        if (!string.IsNullOrWhiteSpace(record.Model))
        {
            raw["gen_ai.response.model"] = JsonSerializer.SerializeToElement(record.Model);
        }

        if (!string.IsNullOrWhiteSpace(record.Agent?.AgentName))
        {
            raw["gen_ai.agent.name"] = JsonSerializer.SerializeToElement(record.Agent.AgentName);
        }

        if (string.Equals(record.AgentInstanceId, CompactBoundaryAgentInstanceId, StringComparison.Ordinal))
        {
            raw[CompactBoundaryRawKey] = JsonSerializer.SerializeToElement(true);
        }

        AddJoinIds(raw, record);
        AddSkills(raw, record);

        long freshInput = 0;
        long cacheRead = 0;
        long cacheCreation = 0;
        long output = 0;
        long? reasoning = null;
        foreach (CollectedTokenCount tokenCount in record.Tokens)
        {
            switch (tokenCount.TokenClass)
            {
                case TokenClass.Input:
                    freshInput = tokenCount.Count;
                    break;
                case TokenClass.CacheRead:
                    cacheRead = tokenCount.Count;
                    break;
                case TokenClass.CacheWrite:
                    cacheCreation = tokenCount.Count;
                    break;
                case TokenClass.Output:
                    output = tokenCount.Count;
                    break;
                case TokenClass.Reasoning:
                    reasoning = tokenCount.Count;
                    break;
            }
        }

        List<ContentPart> parts = new(record.Parts.Count + record.ToolDefinitions.Count);
        foreach (CollectedContentPart part in record.Parts)
        {
            PartTokenFigure figure = TokenFigureOf(part);
            parts.Add(new ContentPart(
                Part: PartKeyOf(part.Bucket),
                Text: part.PlainText ?? string.Empty,
                Tokens: figure.Tokens,
                Server: part.Server,
                Role: part.Role,
                Order: part.Position,
                Resident: part.Resident,
                Truncated: part.OriginalUtf8BytesWhenTruncated.HasValue,
                Tokenizer: figure.Tokenizer,
                TokenizerIsModelOwn: figure.TokenizerIsModelOwn));
        }

        foreach (CollectedToolDefinition definition in record.ToolDefinitions)
        {
            parts.Add(new ContentPart(
                Part: "tool_definitions",
                Text: definition.ToolName,
                Tokens: definition.SchemaTokens,
                Server: definition.Server,
                Order: TypedDefinitionOrderBase + definition.Position,
                Tokenizer: definition.Tokenizer,
                TokenizerIsModelOwn: definition.Tokenizer is null ? null : definition.TokenizerIsModelOwn));
        }

        parts.Sort(static (left, right) => (left.Order ?? 0).CompareTo(right.Order ?? 0));

        return new CanonicalRecord
        {
            SpanId = record.RecordId,
            TraceId = record.TraceId,
            ParentSpanId = record.ParentRecordId,
            Source = record.SourceName ?? RelationalSourceName,
            Harness = record.HarnessId,
            SessionId = record.SessionKey,
            Name = record.Name,
            Op = CanonRecordVocabulary.FormatRecordOp(record.Op),
            Kind = CanonRecordVocabulary.FormatRecordKind(record.Kind),
            Timestamp = record.StartedAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            DurationMs = record.ReportedDurationMs ?? 0,
            Status = CanonRecordVocabulary.FormatRecordStatus(record.Status),
            Tokens = new TokenUsage(
                FreshInput: freshInput,
                CacheRead: cacheRead,
                CacheCreation: cacheCreation,
                Output: output,
                ReportedInput: record.ReportedInputTokens ?? 0,
                ReportedOutput: record.ReportedOutputTokens ?? 0,
                Reasoning: reasoning),
            Content = CanonValidation.ContentFromParts(parts),
            Parts = parts,
            Cost = record.ReportedCost is null
                ? new CostBlock(CostBasis.Unknown, CostStatus.NoRate)
                : new CostBlock(CostBasis.Harness, CostStatus.Priced, record.ReportedCost.Amount, record.ReportedCost.Currency),
            Measurability = null,
            Raw = raw,
        };
    }

    /// <summary>
    /// Puts the record's stored response id and request id under the keys the projector's id join
    /// reads for the record's side: <c>message.id</c> and <c>request_id</c> for an OTLP record,
    /// <c>provenance.nativeRecordId</c> and <c>requestId</c> for a file record.
    /// </summary>
    private static void AddJoinIds(Dictionary<string, JsonElement> raw, CollectedRecord record)
    {
        bool fileSource = record.SourceName is not null
            && record.SourceName.StartsWith(CanonSynth.FileSourcePrefix, StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(record.ResponseId))
        {
            if (fileSource)
            {
                raw["provenance"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["nativeRecordId"] = record.ResponseId });
            }
            else
            {
                raw["message.id"] = JsonSerializer.SerializeToElement(record.ResponseId);
            }
        }

        if (!string.IsNullOrWhiteSpace(record.RequestId))
        {
            raw[fileSource ? "requestId" : "request_id"] = JsonSerializer.SerializeToElement(record.RequestId);
        }
    }

    /// <summary>
    /// Puts the record's stored skill invocations and skill listing under the attributes the
    /// inactive-skill-reference detector reads: <c>kyber.skills.invoked</c> (a string array) and
    /// <c>kyber.skills.listed</c> (<c>{ skills: [{ name }] }</c>, in listing order). Each key is
    /// written only when the record carries at least one name, as <see cref="CanonSynth"/> does, so a
    /// record with neither leaves the detector's measurability unchanged. The store keeps neither the
    /// listing's <c>isInitial</c> flag nor skill descriptions, so both are left out.
    /// </summary>
    private static void AddSkills(Dictionary<string, JsonElement> raw, CollectedRecord record)
    {
        if (record.SkillsInvoked is { Count: > 0 } invoked)
        {
            raw[CanonSynth.SkillsInvokedKey] = JsonSerializer.SerializeToElement(invoked.Distinct(StringComparer.Ordinal).ToArray());
        }

        if (record.SkillsListed is { Count: > 0 } listed)
        {
            ListedSkillsPayload payload = new(IsInitial: null, [.. listed.Select(static name => new ListedSkillItem(name))]);
            raw[CanonSynth.SkillsListedKey] = JsonSerializer.SerializeToElement(payload);
        }
    }

    /// <summary>
    /// The token figure a content part shows: a harness-reported count passes through unlabelled; a
    /// count measured from withheld text shows only with the tokenizer that produced it, and a
    /// stored measured count with no label reads as not measured (decision D17).
    /// </summary>
    /// <param name="part">Content part to read the figure from.</param>
    /// <returns>The count and, for a text-derived count, its tokenizer label.</returns>
    public static PartTokenFigure TokenFigureOf(CollectedContentPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.ReportedTokens is long reported)
        {
            return new PartTokenFigure(reported, null, null);
        }

        return part.Withheld is { Tokenizer: string tokenizer } withheld
            ? new PartTokenFigure(withheld.MeasuredTokens, tokenizer, withheld.TokenizerIsModelOwn)
            : new PartTokenFigure(null, null, null);
    }

    /// <summary>Maps a collected content-part bucket to the canonical part key the pipeline uses.</summary>
    /// <param name="bucket">Bucket recorded on the content part.</param>
    /// <returns>The canonical part key, which is also the stored bucket name.</returns>
    public static string PartKeyOf(ContentBucket bucket) => bucket switch
    {
        ContentBucket.SystemPrompt => "system_prompt",
        ContentBucket.ToolDefinitions => "tool_definitions",
        ContentBucket.ToolDefinition => "tool_definition",
        ContentBucket.InstructionContext => "instruction_context",
        ContentBucket.ConversationHistory => "conversation_history",
        ContentBucket.ToolResultContent => "tool_result_content",
        ContentBucket.UserPrompt => "user_prompt",
        ContentBucket.UserMessage => "user_message",
        _ => "reasoning",
    };
}

/// <summary>The token figure a content part shows, with the tokenizer label a text-derived count carries.</summary>
/// <param name="Tokens">Token count, or <see langword="null"/> when the part has no shown count.</param>
/// <param name="Tokenizer">Tokenizer that counted a text-derived figure; <see langword="null"/> for a reported count.</param>
/// <param name="TokenizerIsModelOwn">Whether <paramref name="Tokenizer"/> is the model's own tokenizer; <see langword="null"/> when <paramref name="Tokenizer"/> is.</param>
public readonly record struct PartTokenFigure(long? Tokens, string? Tokenizer, bool? TokenizerIsModelOwn);
