using System.Globalization;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Warp;

/// <summary>
/// Typed representation of one <c>agent_conversations</c> row in a Warp SQLite export
/// (<c>conversation_id</c>, <c>conversation_data</c>, <c>last_modified_at</c>).
/// </summary>
/// <param name="ConversationId">Native conversation identifier (<c>conversation_id</c>).</param>
/// <param name="ConversationData">Raw conversation usage JSON (<c>conversation_data</c>).</param>
/// <param name="LastModifiedAt">Last-modified timestamp (<c>last_modified_at</c>), when present.</param>
public record WarpConversationRow(
    string ConversationId,
    string ConversationData,
    string? LastModifiedAt);

/// <summary>
/// Typed representation of one <c>ai_queries</c> row in a Warp SQLite export.
/// </summary>
/// <param name="ExchangeId">Native exchange identifier (<c>exchange_id</c>).</param>
/// <param name="ConversationId">Owning conversation identifier (<c>conversation_id</c>).</param>
/// <param name="StartTs">Exchange start timestamp (<c>start_ts</c>).</param>
/// <param name="Input">Raw exchange input JSON (<c>input</c>).</param>
/// <param name="WorkingDirectory">Exchange working directory (<c>working_directory</c>), when present.</param>
/// <param name="OutputStatus">Exchange output status (<c>output_status</c>).</param>
/// <param name="ModelId">Exchange model id (<c>model_id</c>).</param>
/// <param name="PlanningModelId">Planning model id (<c>planning_model_id</c>).</param>
/// <param name="CodingModelId">Coding model id (<c>coding_model_id</c>).</param>
public record WarpQueryRow(
    string ExchangeId,
    string ConversationId,
    string StartTs,
    string Input,
    string? WorkingDirectory,
    string OutputStatus,
    string ModelId,
    string PlanningModelId,
    string CodingModelId);

/// <summary>
/// Typed representation of one <c>blocks</c> row in a Warp SQLite export.
/// </summary>
/// <param name="BlockId">Native block identifier (<c>block_id</c>).</param>
/// <param name="StartTs">Block start timestamp (<c>start_ts</c>), when present.</param>
/// <param name="StylizedCommand">Decoded command text (<c>stylized_command</c>), when present.</param>
public record WarpBlockRow(
    string BlockId,
    string? StartTs,
    string? StylizedCommand);

/// <summary>
/// Non-fatal parse problem encountered while reading a Warp SQLite export.
/// </summary>
/// <param name="LineNumber">1-based row index within its section (0 for document-level failures).</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record WarpParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Parsed Warp per-conversation SQLite export produced by <see cref="WarpMapper.Parse"/>:
/// one <c>agent_conversations</c> row plus its <c>ai_queries</c> and <c>blocks</c> rows.
/// </summary>
/// <param name="IsValid">Whether the envelope held a conversation row (malformed envelopes yield problems instead of records).</param>
/// <param name="Conversation">The exported conversation row, or <see langword="null"/> when absent.</param>
/// <param name="Queries">Exported query rows in order.</param>
/// <param name="Blocks">Exported block rows in order.</param>
/// <param name="ParseProblems">Non-fatal parse diagnostics.</param>
public record WarpExportDocument(
    bool IsValid,
    WarpConversationRow? Conversation,
    IReadOnlyList<WarpQueryRow> Queries,
    IReadOnlyList<WarpBlockRow> Blocks,
    IReadOnlyList<WarpParseProblem> ParseProblems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting <c>dash/src/providers/warp.ts</c> into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// The offline seam parses a per-conversation JSON export mirroring the <c>agent_conversations</c>,
/// <c>ai_queries</c>, and <c>blocks</c> projections; opening SQLite files in unit tests is forbidden
/// (OD-9), so the export rows arrive as text. Warp reports token usage per conversation and model,
/// never per exchange, so the conversation totals (primary-agent category, falling back to each
/// entry's Warp plus BYOK total) become the session's reported usage and each completed exchange
/// maps to one <c>llm.invoke</c> record whose tokens are not measured: a split of the conversation
/// total across exchanges would be an estimate, which KyberDash never produces (decision D15).
/// Warp's figure is a single total with no input/output breakdown; it is kept in the
/// <see cref="TokenClass.Input"/> class, the class Warp's figures have always been stored in.
/// Each attributed command block maps to a child <c>tool.invoke</c>
/// span named <c>Bash</c> carrying the full command text. Live-SQLite-only surfaces (schema
/// validation, discovery, access-denial draining, workspace maps, <c>run_command</c> display-name
/// mapping, and <c>calculateCost</c> pricing) have no offline input: costs stay unreported so
/// rate-table estimation applies, and the derived per-exchange tool-name/<c>bashCommands</c>
/// arrays fold into the child <c>tool_call</c> rows.
/// </remarks>
public class WarpMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for Warp.</summary>
    public const string WarpHarnessId = "warp";

    /// <summary>Diagnostic code emitted when a Warp export cannot be parsed.</summary>
    public const string MalformedDocumentCode = "WARP_MALFORMED_DOCUMENT";

    /// <summary>Tool name recorded for attributed command blocks.</summary>
    public const string BashToolName = "Bash";

    private const string PrimaryAgentCategory = "primary_agent";
    private const string DefaultModelId = "warp-auto-efficient";

    private static readonly IReadOnlyDictionary<string, string> ModelAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Claude Sonnet 4.6"] = "claude-sonnet-4-6",
            ["Claude Sonnet 4.5"] = "claude-sonnet-4-5",
            ["Claude Haiku 4.5"] = "claude-haiku-4-5",
            ["Claude Opus 4.6"] = "claude-opus-4-6",
            ["GPT-5.3 Codex (low reasoning)"] = "gpt-5.3-codex",
            ["GPT-5.3 Codex (medium reasoning)"] = "gpt-5.3-codex",
            ["GPT-5.3 Codex (high reasoning)"] = "gpt-5.3-codex",
            ["GPT-5.3 Codex (extra high reasoning)"] = "gpt-5.3-codex",
            ["auto-efficient"] = "warp-auto-efficient",
            ["auto-powerful"] = "warp-auto-powerful",
        };

    /// <inheritdoc />
    public string HarnessId => WarpHarnessId;

    /// <summary>
    /// Parses a per-conversation Warp SQLite export into a strongly typed
    /// <see cref="WarpExportDocument"/>. Never throws: malformed envelopes yield an
    /// invalid document carrying a <see cref="WarpParseProblem"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the export envelope.</param>
    /// <returns>The parsed export document.</returns>
    public static WarpExportDocument Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return Invalid("Skipping empty Warp export: expected a JSON object with a conversation envelope.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException ex)
        {
            return Invalid($"Skipping malformed Warp export: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("Skipping Warp export: expected a top-level JSON object.");
            }

            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("conversation", out JsonElement conversationEl)
                || conversationEl.ValueKind != JsonValueKind.Object)
            {
                return Invalid("Skipping Warp export: expected a conversation object.");
            }

            string? conversationId = ReadOptionalString(conversationEl, "conversation_id");
            if (string.IsNullOrEmpty(conversationId))
            {
                return Invalid("Skipping Warp export: conversation row has no conversation_id.");
            }

            WarpConversationRow conversation = new(
                ConversationId: conversationId,
                ConversationData: ReadStringOrEmpty(conversationEl, "conversation_data"),
                LastModifiedAt: ReadOptionalString(conversationEl, "last_modified_at"));

            List<WarpQueryRow> queries = [];
            List<WarpParseProblem> problems = [];
            if (root.TryGetProperty("queries", out JsonElement queriesEl)
                && queriesEl.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement queryEl in queriesEl.EnumerateArray())
                {
                    index++;
                    if (queryEl.ValueKind != JsonValueKind.Object)
                    {
                        problems.Add(new WarpParseProblem(
                            index,
                            MalformedDocumentCode,
                            string.Create(CultureInfo.InvariantCulture, $"Skipping non-object Warp query row {index}.")));
                        continue;
                    }

                    string? exchangeId = ReadOptionalString(queryEl, "exchange_id");
                    if (string.IsNullOrEmpty(exchangeId))
                    {
                        problems.Add(new WarpParseProblem(
                            index,
                            MalformedDocumentCode,
                            string.Create(CultureInfo.InvariantCulture, $"Skipping Warp query row {index} without an exchange_id.")));
                        continue;
                    }

                    queries.Add(new WarpQueryRow(
                        ExchangeId: exchangeId,
                        ConversationId: ReadOptionalString(queryEl, "conversation_id") ?? conversationId,
                        StartTs: ReadStringOrEmpty(queryEl, "start_ts"),
                        Input: ReadStringOrEmpty(queryEl, "input"),
                        WorkingDirectory: ReadOptionalString(queryEl, "working_directory"),
                        OutputStatus: ReadStringOrEmpty(queryEl, "output_status"),
                        ModelId: ReadStringOrEmpty(queryEl, "model_id"),
                        PlanningModelId: ReadStringOrEmpty(queryEl, "planning_model_id"),
                        CodingModelId: ReadStringOrEmpty(queryEl, "coding_model_id")));
                }
            }

            List<WarpBlockRow> blocks = [];
            if (root.TryGetProperty("blocks", out JsonElement blocksEl)
                && blocksEl.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement blockEl in blocksEl.EnumerateArray())
                {
                    index++;
                    if (blockEl.ValueKind != JsonValueKind.Object)
                    {
                        problems.Add(new WarpParseProblem(
                            index,
                            MalformedDocumentCode,
                            string.Create(CultureInfo.InvariantCulture, $"Skipping non-object Warp block row {index}.")));
                        continue;
                    }

                    string? blockId = ReadOptionalString(blockEl, "block_id");
                    if (string.IsNullOrEmpty(blockId))
                    {
                        problems.Add(new WarpParseProblem(
                            index,
                            MalformedDocumentCode,
                            string.Create(CultureInfo.InvariantCulture, $"Skipping Warp block row {index} without a block_id.")));
                        continue;
                    }

                    blocks.Add(new WarpBlockRow(
                        BlockId: blockId,
                        StartTs: ReadOptionalString(blockEl, "start_ts"),
                        StylizedCommand: ReadOptionalString(blockEl, "stylized_command")));
                }
            }

            return new WarpExportDocument(true, conversation, queries, blocks, problems);
        }
    }

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        CollectedSource source = new(WarpHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);
        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        WarpExportDocument document = Parse(unit.RawText);
        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, importedAtUtc);

        if (!document.IsValid || document.Conversation is null)
        {
            return new CollectedBatch(source, [], [], problems);
        }

        string conversationId = DecodeConversationId(unit.Unit.SourceKey);
        if (conversationId.Length == 0 || !string.Equals(conversationId, document.Conversation.ConversationId, StringComparison.Ordinal))
        {
            return new CollectedBatch(source, [], [], problems);
        }

        List<ParsedExchange> exchanges = [];
        foreach (WarpQueryRow query in document.Queries)
        {
            if (!IsFinalStatus(query.OutputStatus))
            {
                continue;
            }

            DateTimeOffset? start = ParseTimestamp(query.StartTs);
            if (!start.HasValue)
            {
                continue;
            }

            exchanges.Add(new ParsedExchange(query, start.Value));
        }

        exchanges.Sort(static (a, b) => a.StartedAt.CompareTo(b.StartedAt));

        (IReadOnlyList<CollectedModelUsage> reportedUsage, string dominantModel) =
            ExtractConversationUsage(document.Conversation.ConversationData);
        IReadOnlyDictionary<string, IReadOnlyList<WarpBlockRow>> blocksByExchange =
            AssignCommandBlocksToExchanges(document.Blocks, exchanges);

        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{WarpHarnessId}:{conversationId}");
        string? workspaceDirectory = SelectWorkspaceDirectory(exchanges);

        List<CollectedRecord> records = [];
        HashSet<string> seenAnchors = new(StringComparer.Ordinal);
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        for (int index = 0; index < exchanges.Count; index++)
        {
            ParsedExchange exchange = exchanges[index];
            string anchor = exchange.Row.ExchangeId;
            if (!seenAnchors.Add(anchor))
            {
                continue;
            }

            string model = ResolveModelForExchange(exchange.Row, dominantModel);
            string userMessage = ExtractUserMessage(exchange.Row.Input);
            if (userMessage.Length > 500)
            {
                userMessage = userMessage.Substring(0, 500);
            }

            string parentRecordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(CultureInfo.InvariantCulture, $"{traceId}:{anchor}"),
                index + 1);

            List<CollectedContentPart> parts = [];
            string? correctionRule = null;
            if (userMessage.Length > 0)
            {
                CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(userMessage);
                if (check.Matched)
                {
                    correctionRule = check.Rule;
                }

                parts.Add(ToolContentMeasurer.BuildContentPart(0, ContentBucket.UserMessage, userMessage, "user"));
            }

            string? projectPath = SafeOptionalText(exchange.Row.WorkingDirectory);

            CollectedRecord parentRecord = new(
                parentRecordId,
                WarpHarnessId,
                RecordOp.LlmInvoke,
                RecordKind.Internal,
                string.Create(CultureInfo.InvariantCulture, $"{WarpHarnessId}:{model}"),
                exchange.StartedAt.ToUniversalTime(),
                RecordStatus.Ok,
                conversationId,
                null,
                traceId,
                model,
                null,
                null,
                [],
                null,
                parts,
                null,
                null,
                [],
                null,
                [],
                new CollectedRecordSignals(null, null, correctionRule, null));

            records.Add(parentRecord);

            if (blocksByExchange.TryGetValue(anchor, out IReadOnlyList<WarpBlockRow>? attributed))
            {
                for (int b = 0; b < attributed.Count; b++)
                {
                    records.Add(BuildBashToolRecord(parentRecord, conversationId, traceId, attributed[b], b, seenRecordIds));
                }
            }
        }

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: conversationId,
                HarnessId: WarpHarnessId,
                NativeSessionId: conversationId,
                Workspace: workspaceDirectory is null ? null : new CollectedWorkspace(workspaceDirectory, null, null),
                Title: null,
                ReportedUsage: reportedUsage),
        ];

        return new CollectedBatch(source, sessions, records, problems);
    }

    /// <summary>
    /// Normalizes a raw Warp model id through the static alias table, passing
    /// unknown ids through unchanged.
    /// </summary>
    /// <param name="rawModel">Raw model id.</param>
    /// <returns>The normalized model id.</returns>
    public static string NormalizeModel(string? rawModel)
    {
        string model = rawModel?.Trim() ?? string.Empty;
        if (model.Length == 0)
        {
            return model;
        }

        return ModelAliases.TryGetValue(model, out string? aliased) ? aliased : model;
    }

    /// <summary>
    /// Sanitizes a working directory into a project slug by stripping leading slashes
    /// and replacing remaining slashes with dashes.
    /// </summary>
    /// <param name="projectPath">Raw working directory.</param>
    /// <returns>The sanitized project slug.</returns>
    public static string SanitizeProject(string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        return projectPath.TrimStart('/').Replace('/', '-');
    }

    private sealed record ParsedExchange(WarpQueryRow Row, DateTimeOffset StartedAt);

    private static WarpExportDocument Invalid(string message) =>
        new(
            IsValid: false,
            Conversation: null,
            Queries: [],
            Blocks: [],
            ParseProblems: [new WarpParseProblem(0, MalformedDocumentCode, message)]);

    private static string DecodeConversationId(string sourceKey)
    {
        int splitIndex = sourceKey.LastIndexOf(':');
        if (splitIndex <= 0)
        {
            return string.Empty;
        }

        return sourceKey.Substring(splitIndex + 1).Trim();
    }

    private static bool IsFinalStatus(string rawStatus)
    {
        string status = ParseJsonString(rawStatus);
        return string.Equals(status, "Completed", StringComparison.Ordinal)
            || string.Equals(status, "Cancelled", StringComparison.Ordinal)
            || string.Equals(status, "Failed", StringComparison.Ordinal);
    }

    private static string ParseJsonString(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                return document.RootElement.GetString() ?? raw;
            }

            return raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        string withT = trimmed.Contains('T', StringComparison.Ordinal) ? trimmed : ReplaceFirstSpace(trimmed);
        int lastPlus = withT.LastIndexOf('+');
        int lastMinus = withT.LastIndexOf('-');
        bool hasOffset = lastPlus > 9 || lastMinus > 9;
        bool hasTimezone = withT.EndsWith('Z') || hasOffset;
        string normalized = hasTimezone ? withT : withT + "Z";

        if (DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string ReplaceFirstSpace(string value)
    {
        int index = value.IndexOf(' ', StringComparison.Ordinal);
        return index < 0 ? value : string.Concat(value.AsSpan(0, index), "T", value.AsSpan(index + 1));
    }

    /// <summary>
    /// Reads the conversation's reported token usage into per-model session usage rows, and the
    /// model that carried the most of it.
    /// </summary>
    /// <remarks>
    /// Each <c>token_usage</c> entry is one model's reported figures. When any entry reports the
    /// <c>primary_agent</c> category, the usage is that category per model; otherwise it is each
    /// entry's <c>warp_tokens</c> plus <c>byok_tokens</c>. Entries for the same model are summed,
    /// which is arithmetic on reported values, not an estimate. An entry with no model id is
    /// attributed to the dominant model (or <c>warp-auto-efficient</c> when none is known). No
    /// reported usage yields no rows: the figure is then not measured. Warp reports one total per
    /// model with no input/output split, so the rows are the <c>total</c> token class, never
    /// <c>input</c> (decision D15).
    /// </remarks>
    /// <param name="conversationData">The conversation's <c>conversation_data</c> JSON.</param>
    /// <returns>Per-model usage rows at snapshot position 0, and the normalized dominant model.</returns>
    private static (IReadOnlyList<CollectedModelUsage> Usage, string DominantModel) ExtractConversationUsage(string conversationData)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(conversationData);
        }
        catch (JsonException)
        {
            return ([], string.Empty);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("conversation_usage_metadata", out JsonElement usageMetadataEl)
                || usageMetadataEl.ValueKind != JsonValueKind.Object
                || !usageMetadataEl.TryGetProperty("token_usage", out JsonElement tokenUsageEl)
                || tokenUsageEl.ValueKind != JsonValueKind.Array)
            {
                return ([], string.Empty);
            }

            long dominantPrimaryTokens = 0L;
            long dominantFallbackTokens = 0L;
            string dominantModel = string.Empty;
            List<(string? Model, long PrimaryTokens, long EntryTotal)> entries = [];

            foreach (JsonElement entry in tokenUsageEl.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                long primaryTokens = ExtractCategoryTokens(entry, "warp_token_usage_by_category")
                    + ExtractCategoryTokens(entry, "byok_token_usage_by_category");
                long entryTotal = ReadSafeLong(entry, "warp_tokens") + ReadSafeLong(entry, "byok_tokens");

                string? reportedModel = entry.TryGetProperty("model_id", out JsonElement modelEl)
                    && modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString()
                    : null;
                entries.Add((reportedModel, primaryTokens, entryTotal));

                string entryModel = reportedModel ?? dominantModel;
                if (primaryTokens > dominantPrimaryTokens)
                {
                    dominantPrimaryTokens = primaryTokens;
                    dominantModel = entryModel;
                }

                if (dominantPrimaryTokens == 0 && entryTotal > dominantFallbackTokens)
                {
                    dominantFallbackTokens = entryTotal;
                    dominantModel = entryModel;
                }
            }

            string normalizedDominant = NormalizeModel(dominantModel);
            bool usePrimary = entries.Any(static entry => entry.PrimaryTokens > 0);
            List<string> modelOrder = [];
            Dictionary<string, long> countsByModel = new(StringComparer.Ordinal);
            foreach ((string? model, long primaryTokens, long entryTotal) in entries)
            {
                long count = usePrimary ? primaryTokens : entryTotal;
                if (count <= 0)
                {
                    continue;
                }

                string normalized = NormalizeModel(model);
                if (normalized.Length == 0)
                {
                    normalized = normalizedDominant.Length > 0 ? normalizedDominant : DefaultModelId;
                }

                if (countsByModel.TryGetValue(normalized, out long existing))
                {
                    countsByModel[normalized] = existing + count;
                }
                else
                {
                    modelOrder.Add(normalized);
                    countsByModel[normalized] = count;
                }
            }

            List<CollectedModelUsage> usage =
            [
                .. modelOrder.Select(model => new CollectedModelUsage(0, model, TokenClass.Total, countsByModel[model])),
            ];
            return (usage, normalizedDominant);
        }
    }

    private static long ExtractCategoryTokens(JsonElement entry, string key)
    {
        if (!entry.TryGetProperty(key, out JsonElement categoriesEl)
            || categoriesEl.ValueKind != JsonValueKind.Object
            || !categoriesEl.TryGetProperty(PrimaryAgentCategory, out JsonElement categoryEl))
        {
            return 0;
        }

        return ReadSafeLong(categoryEl);
    }

    private static string ExtractUserMessage(string rawInput)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawInput);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("Query", out JsonElement queryEl)
                    || queryEl.ValueKind != JsonValueKind.Object
                    || !queryEl.TryGetProperty("text", out JsonElement textEl)
                    || textEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return string.Empty;
        }
    }

    private static string ResolveModelForExchange(WarpQueryRow exchange, string dominantModel)
    {
        string candidate = FirstPresentString(
            exchange.ModelId,
            exchange.CodingModelId,
            exchange.PlanningModelId,
            dominantModel,
            DefaultModelId) ?? DefaultModelId;
        string normalized = NormalizeModel(candidate);
        if ((string.Equals(normalized, "warp-auto-efficient", StringComparison.Ordinal)
                || string.Equals(normalized, "warp-auto-powerful", StringComparison.Ordinal))
            && dominantModel.Length > 0)
        {
            return dominantModel;
        }

        return normalized;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<WarpBlockRow>> AssignCommandBlocksToExchanges(
        IReadOnlyList<WarpBlockRow> blocks,
        IReadOnlyList<ParsedExchange> exchanges)
    {
        Dictionary<string, List<WarpBlockRow>> toolsByExchange = new(StringComparer.Ordinal);
        foreach (WarpBlockRow block in blocks.OrderBy(static b => ParseTimestampForOrdering(b.StartTs)))
        {
            DateTimeOffset? blockStart = ParseTimestamp(block.StartTs);
            if (!blockStart.HasValue)
            {
                continue;
            }

            ParsedExchange? target = null;
            foreach (ParsedExchange exchange in exchanges)
            {
                if (exchange.StartedAt > blockStart.Value)
                {
                    break;
                }

                target = exchange;
            }

            if (target is null)
            {
                continue;
            }

            if (!toolsByExchange.TryGetValue(target.Row.ExchangeId, out List<WarpBlockRow>? attributed))
            {
                attributed = [];
                toolsByExchange[target.Row.ExchangeId] = attributed;
            }

            attributed.Add(block);
        }

        return toolsByExchange.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<WarpBlockRow>)entry.Value,
            StringComparer.Ordinal);
    }

    private static long ParseTimestampForOrdering(string? raw)
    {
        DateTimeOffset? parsed = ParseTimestamp(raw);
        return parsed?.UtcTicks ?? long.MinValue;
    }

    private static string? SelectWorkspaceDirectory(IReadOnlyList<ParsedExchange> exchanges)
    {
        string? workspace = null;
        DateTimeOffset latest = DateTimeOffset.MinValue;
        foreach (ParsedExchange exchange in exchanges)
        {
            string? workingDirectory = SafeOptionalText(exchange.Row.WorkingDirectory);
            if (workingDirectory is null)
            {
                continue;
            }

            if (workspace is null || exchange.StartedAt > latest)
            {
                workspace = workingDirectory;
                latest = exchange.StartedAt;
            }
        }

        return workspace;
    }

    private static CollectedRecord BuildBashToolRecord(
        CollectedRecord parentRecord,
        string sessionId,
        string traceId,
        WarpBlockRow block,
        int blockIndex,
        HashSet<string> seenRecordIds)
    {
        string childRecordId = EnsureUniqueRecordId(
            seenRecordIds,
            string.Create(CultureInfo.InvariantCulture, $"{parentRecord.RecordId}-bash-{block.BlockId}"),
            blockIndex);

        CollectedToolContent? inputContent = string.IsNullOrEmpty(block.StylizedCommand)
            ? null
            : ToolContentMeasurer.Measure(block.StylizedCommand, CanonSynth.MaxToolArgumentsBytes)
                .ToToolContent(includeFingerprint: true);

        CollectedToolCall collectedTool = new(
            block.BlockId,
            BashToolName,
            null,
            null,
            inputContent,
            null,
            null,
            null,
            null);

        return new CollectedRecord(
            childRecordId,
            WarpHarnessId,
            RecordOp.ToolInvoke,
            RecordKind.Internal,
            BashToolName,
            ParseTimestamp(block.StartTs) ?? parentRecord.StartedAt,
            RecordStatus.Unset,
            sessionId,
            parentRecord.RecordId,
            traceId,
            null,
            null,
            null,
            [],
            null,
            [],
            collectedTool,
            null,
            [],
            null,
            [],
            new CollectedRecordSignals(null, null, null, null));
    }

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator) =>
        HarnessJson.EnsureUniqueRecordId(seenRecordIds, candidate, disambiguator);

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<WarpParseProblem> parseProblems,
        string sourceKey,
        DateTimeOffset importedAtUtc)
    {
        if (parseProblems.Count == 0)
        {
            return [];
        }

        string timestampUtc = importedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        List<CollectedProblem> problems = new(parseProblems.Count);

        foreach (WarpParseProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"warp-malformed:{sourceKey}:row-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed Warp export.";

            problems.Add(new CollectedProblem(
                problemKey,
                "warning",
                parseProblem.Code,
                message,
                null,
                location,
                null,
                WarpHarnessId,
                timestampUtc,
                null));
        }

        return problems;
    }

    private static string? FirstPresentString(params string?[] values) =>
        HarnessJson.FirstPresentString(values);

    private static long ReadSafeLong(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        if (el.TryGetInt64(out long value) && value > 0)
        {
            return value;
        }

        if (el.TryGetDouble(out double doubleVal) && double.IsFinite(doubleVal) && doubleVal > 0)
        {
            return (long)doubleVal;
        }

        return 0;
    }

    private static long ReadSafeLong(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        if (element.TryGetInt64(out long value) && value > 0)
        {
            return value;
        }

        if (element.TryGetDouble(out double doubleVal) && double.IsFinite(doubleVal) && doubleVal > 0)
        {
            return (long)doubleVal;
        }

        return 0;
    }

    private static string? ReadOptionalString(JsonElement obj, string key) =>
        HarnessJson.ReadOptionalString(obj, key);

    private static string ReadStringOrEmpty(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el))
        {
            return string.Empty;
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);
}
