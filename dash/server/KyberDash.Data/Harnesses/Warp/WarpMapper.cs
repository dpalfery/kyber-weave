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
/// (OD-9), so the export rows arrive as text. Each completed exchange maps to one <c>llm.invoke</c>
/// record with tokens allocated from the conversation-level primary-agent budget (falling back to
/// input-length weights), and each attributed command block maps to a child <c>tool.invoke</c>
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
    private const int CharsPerToken = 4;

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

        (long tokenBudget, string dominantModel) = ExtractTokenBudget(document.Conversation.ConversationData);
        long[] weights = exchanges.Select(static exchange => EstimateWeight(exchange.Row.Input)).ToArray();
        long fallbackBudget = weights.Sum();
        long[] allocatedTokens = AllocateTokens(weights, tokenBudget > 0 ? tokenBudget : fallbackBudget);
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
            long inputTokens = allocatedTokens[index];
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

                parts.Add(BuildContentPart(0, ContentBucket.UserMessage, userMessage, "user"));
            }

            List<CollectedTokenCount> tokens = inputTokens > 0
                ? [new CollectedTokenCount(TokenClass.Input, inputTokens)]
                : [];

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
                tokens,
                null,
                parts,
                null,
                null,
                [],
                null,
                [],
                new CollectedRecordSignals(null, null, correctionRule, null),
                inputTokens,
                null);

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
                ReportedUsage: []),
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

    private static (long TokenBudget, string DominantModel) ExtractTokenBudget(string conversationData)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(conversationData);
        }
        catch (JsonException)
        {
            return (0, string.Empty);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("conversation_usage_metadata", out JsonElement usageMetadataEl)
                || usageMetadataEl.ValueKind != JsonValueKind.Object
                || !usageMetadataEl.TryGetProperty("token_usage", out JsonElement tokenUsageEl)
                || tokenUsageEl.ValueKind != JsonValueKind.Array)
            {
                return (0, string.Empty);
            }

            long primaryTotal = 0L;
            long fallbackTotal = 0L;
            long dominantPrimaryTokens = 0L;
            long dominantFallbackTokens = 0L;
            string dominantModel = string.Empty;

            foreach (JsonElement entry in tokenUsageEl.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                long primaryTokens = ExtractCategoryTokens(entry, "warp_token_usage_by_category")
                    + ExtractCategoryTokens(entry, "byok_token_usage_by_category");
                long entryTotal = ReadSafeLong(entry, "warp_tokens") + ReadSafeLong(entry, "byok_tokens");

                primaryTotal += primaryTokens;
                fallbackTotal += entryTotal;

                string entryModel = entry.TryGetProperty("model_id", out JsonElement modelEl)
                    && modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString() ?? dominantModel
                    : dominantModel;
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

            long tokenBudget = primaryTotal > 0 ? primaryTotal : fallbackTotal;
            return (Math.Max(0, tokenBudget), NormalizeModel(dominantModel));
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

    private static long EstimateWeight(string rawInput)
    {
        string userMessage = ExtractUserMessage(rawInput);
        string sourceText = userMessage.Length > 0 ? userMessage : rawInput;
        long tokens = (sourceText.Length + CharsPerToken - 1) / CharsPerToken;
        return Math.Max(1, tokens);
    }

    private static long[] AllocateTokens(long[] weights, long tokenBudget)
    {
        if (weights.Length == 0)
        {
            return [];
        }

        long[] normalized = weights.Select(static w => Math.Max(0, w)).ToArray();
        long totalWeight = normalized.Sum();
        long budget = Math.Max(0, tokenBudget);

        if (budget == 0)
        {
            return new long[normalized.Length];
        }

        if (totalWeight == 0)
        {
            long even = budget / normalized.Length;
            long[] allocated = normalized.Select(_ => even).ToArray();
            long remainder = budget - (even * normalized.Length);
            int index = 0;
            while (remainder > 0)
            {
                allocated[index] = allocated[index] + 1;
                remainder--;
                index = (index + 1) % normalized.Length;
            }

            return allocated;
        }

        double[] rawAllocation = normalized.Select(weight => ((double)budget * weight) / totalWeight).ToArray();
        long[] floored = rawAllocation.Select(static value => (long)Math.Floor(value)).ToArray();
        long leftover = budget - floored.Sum();

        int[] byLargestFraction = rawAllocation
            .Select((value, i) => (Index: i, Fraction: value - Math.Floor(value)))
            .OrderByDescending(static entry => entry.Fraction)
            .Select(static entry => entry.Index)
            .ToArray();

        int pointer = 0;
        while (leftover > 0 && byLargestFraction.Length > 0)
        {
            floored[byLargestFraction[pointer]] = floored[byLargestFraction[pointer]] + 1;
            leftover--;
            pointer = (pointer + 1) % byLargestFraction.Length;
        }

        return floored;
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

    private static CollectedContentPart BuildContentPart(
        int position,
        ContentBucket bucket,
        string rawText,
        string role)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(rawText, CanonSynth.MaxToolResultBytes);
        string? plainText = measurement.IsDocument ? null : measurement.PlainText;
        CollectedWithheldText? withheld = measurement.IsDocument ? measurement.ToWithheldText() : null;
        long? originalBytes = measurement.IsDocument ? null : measurement.OriginalUtf8BytesWhenTruncated;

        return new CollectedContentPart(
            position,
            bucket,
            plainText,
            withheld,
            null,
            null,
            role,
            null,
            originalBytes);
    }

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator)
    {
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

    private static string? FirstPresentString(params string?[] values)
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

    private static string? ReadOptionalString(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = el.GetString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

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

    private static string? SafeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return JsonDocumentText.IsDocument(trimmed) ? null : trimmed;
    }
}
