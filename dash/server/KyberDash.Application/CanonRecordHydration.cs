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

        AddJoinIds(raw, record);

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
            parts.Add(new ContentPart(
                Part: PartKeyOf(part.Bucket),
                Text: part.PlainText ?? string.Empty,
                Tokens: part.ReportedTokens ?? part.Withheld?.MeasuredTokens,
                Server: part.Server,
                Role: part.Role,
                Order: part.Position,
                Resident: part.Resident,
                Truncated: part.OriginalUtf8BytesWhenTruncated.HasValue));
        }

        foreach (CollectedToolDefinition definition in record.ToolDefinitions)
        {
            parts.Add(new ContentPart(
                Part: "tool_definitions",
                Text: definition.ToolName,
                Tokens: definition.SchemaTokens,
                Server: definition.Server,
                Order: TypedDefinitionOrderBase + definition.Position));
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
    /// reads for the record's side: <c>gen_ai.response.id</c> and <c>request_id</c> for an OTLP
    /// record, <c>provenance.nativeRecordId</c> and <c>requestId</c> for a file record.
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
                raw["gen_ai.response.id"] = JsonSerializer.SerializeToElement(record.ResponseId);
            }
        }

        if (!string.IsNullOrWhiteSpace(record.RequestId))
        {
            raw[fileSource ? "requestId" : "request_id"] = JsonSerializer.SerializeToElement(record.RequestId);
        }
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
