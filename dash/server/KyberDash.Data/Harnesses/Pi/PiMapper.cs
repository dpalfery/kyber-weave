using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Pi;

/// <summary>
/// Typed representation of a <c>toolCall</c> block inside a Pi or Oh My Pi (OMP) assistant message.
/// </summary>
/// <param name="CallId">Optional native tool call identifier (<c>id</c>).</param>
/// <param name="Name">Raw native tool name (<c>name</c>).</param>
/// <param name="RawInputText">Raw JSON arguments payload text, when present.</param>
/// <param name="Path">Optional <c>arguments.path</c> string used for skill-load classification.</param>
/// <param name="FilePath">Optional <c>arguments.file_path</c> string used as a skill-load path fallback.</param>
/// <param name="Command">Optional <c>arguments.command</c> string for <c>bash</c> tool calls.</param>
public record PiToolCall(
    string? CallId,
    string Name,
    string? RawInputText,
    string? Path,
    string? FilePath,
    string? Command);

/// <summary>
/// Typed representation of a content block inside a Pi or OMP transcript message.
/// </summary>
/// <param name="BlockType">Content block type (<c>text</c> or <c>toolCall</c>).</param>
/// <param name="Text">Text payload for <c>text</c> blocks or string <c>content</c>.</param>
/// <param name="ToolCall">Tool call payload for <c>toolCall</c> blocks.</param>
public record PiContentBlock(
    string BlockType,
    string? Text,
    PiToolCall? ToolCall);

/// <summary>
/// Token usage and optional cost counters extracted from a Pi or OMP assistant message.
/// </summary>
/// <param name="InputTokens">Exclusive input tokens (<c>usage.input</c>), when present.</param>
/// <param name="OutputTokens">Generated output tokens (<c>usage.output</c>), when present.</param>
/// <param name="CacheReadTokens">Cache-read tokens (<c>usage.cacheRead</c>), when present.</param>
/// <param name="CacheWriteTokens">Cache-write tokens (<c>usage.cacheWrite</c>), when present.</param>
/// <param name="ReportedCostUsd">Reported total USD cost (<c>usage.cost.total</c>), when present.</param>
public record PiUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    double? ReportedCostUsd);

/// <summary>
/// Typed representation of the <c>message</c> object on a Pi or OMP <c>message</c> JSONL entry.
/// </summary>
/// <param name="Role">Message role (<c>user</c> or <c>assistant</c>).</param>
/// <param name="Model">Optional per-message model identifier (<c>message.model</c>).</param>
/// <param name="ResponseId">Optional upstream response identifier (<c>message.responseId</c>).</param>
/// <param name="AttributionTimestampMs">Optional Unix millisecond attribution timestamp (<c>message.attribution.timestamp</c>).</param>
/// <param name="Usage">Optional usage counters on assistant messages.</param>
/// <param name="Blocks">Normalized content blocks extracted from <c>message.content</c>.</param>
public record PiMessage(
    string? Role,
    string? Model,
    string? ResponseId,
    long? AttributionTimestampMs,
    PiUsage? Usage,
    IReadOnlyList<PiContentBlock> Blocks);

/// <summary>
/// Typed representation of one JSON object line in a Pi or OMP session JSONL file.
/// </summary>
/// <param name="LineNumber">1-based line number within the transcript file.</param>
/// <param name="EntryType">Top-level entry <c>type</c> (<c>session</c>, <c>title</c>, <c>model_change</c>, <c>message</c>, etc.).</param>
/// <param name="Id">Optional native entry identifier (<c>id</c>).</param>
/// <param name="Timestamp">Optional ISO-8601 UTC timestamp string (<c>timestamp</c>).</param>
/// <param name="WorkingDirectory">Optional workspace directory (<c>cwd</c>) on <c>session</c> entries.</param>
/// <param name="Model">Optional model identifier (<c>model</c>) on <c>model_change</c> entries.</param>
/// <param name="Title">Optional session title (<c>title</c>) on OMP <c>title</c> entries.</param>
/// <param name="Message">Optional parsed <c>message</c> payload on <c>message</c> entries.</param>
public record PiEntry(
    int LineNumber,
    string EntryType,
    string? Id,
    string? Timestamp,
    string? WorkingDirectory,
    string? Model,
    string? Title,
    PiMessage? Message);

/// <summary>
/// Non-fatal parse problem encountered while reading a Pi or OMP JSONL line.
/// </summary>
/// <param name="LineNumber">1-based line number within the JSONL file.</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record PiParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Parsed Pi or OMP session JSONL document produced by <see cref="PiMapper.Parse"/>.
/// </summary>
/// <param name="SessionId">Resolved native session identifier from <c>session.id</c>, or <see langword="null"/> when absent.</param>
/// <param name="WorkingDirectory">Workspace directory from <c>session.cwd</c>, or <see langword="null"/> when absent.</param>
/// <param name="Title">Session title from OMP <c>title</c> slot, or <see langword="null"/> when absent.</param>
/// <param name="Entries">Ordered parsed entries from the JSONL file.</param>
/// <param name="ParseProblems">Non-fatal line-level parse problems encountered while reading.</param>
public record PiDocument(
    string? SessionId,
    string? WorkingDirectory,
    string? Title,
    IReadOnlyList<PiEntry> Entries,
    IReadOnlyList<PiParseProblem> ParseProblems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting <c>dash/src/providers/pi.ts</c> into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// Handles both Pi (<c>.pi/agent/sessions</c>) and Oh My Pi (<c>.omp/agent/sessions</c>) JSONL
/// session transcripts: tracks <c>session</c>, <c>title</c>, <c>model_change</c>, and <c>message</c>
/// entries; ignores other Pi/OMP session entry types per the Pi/OMP JSONL format specification;
/// classifies <c>read</c> calls targeting <c>SKILL.md</c> or <c>skill://</c> as <c>Skill</c>
/// invocations; skips zero-token (<c>input == 0 &amp;&amp; output == 0</c>) or timestampless assistant
/// messages; and treats a zero <c>cost.total</c> as absent so OAuth zero placeholders do not override
/// rate-table estimation.
/// </remarks>
public class PiMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for Pi transcripts.</summary>
    public const string PiHarnessId = "pi";

    /// <summary>Canonical harness identifier for Oh My Pi (OMP) transcripts.</summary>
    public const string OmpHarnessId = "omp";

    /// <summary>Diagnostic code emitted when a Pi/OMP JSONL line is malformed.</summary>
    public const string MalformedLineCode = "PI_MALFORMED_LINE";

    private const string DefaultModel = "gpt-5";

    private static readonly IReadOnlyDictionary<string, string> ToolNameMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bash"] = "Bash",
            ["read"] = "Read",
            ["edit"] = "Edit",
            ["write"] = "Write",
            ["glob"] = "Glob",
            ["grep"] = "Grep",
            ["task"] = "Agent",
            ["dispatch_agent"] = "Agent",
            ["fetch"] = "WebFetch",
            ["search"] = "WebSearch",
            ["todo"] = "TodoWrite",
            ["patch"] = "Patch",
        };

    /// <inheritdoc />
    public string HarnessId => PiHarnessId;

    /// <summary>
    /// Parses raw Pi/OMP JSONL transcript text into a strongly typed <see cref="PiDocument"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the session JSONL file.</param>
    /// <param name="sourceKey">Optional path locator reserved for diagnostics.</param>
    /// <returns>The parsed <see cref="PiDocument"/>.</returns>
    public static PiDocument Parse(string? rawText, string? sourceKey = null)
    {
        _ = sourceKey;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new PiDocument(null, null, null, [], []);
        }

        List<PiEntry> entries = [];
        List<PiParseProblem> problems = [];
        string? sessionId = null;
        string? workingDirectory = null;
        string? title = null;

        using StringReader reader = new(rawText);
        string? rawLine;
        int lineNumber = 0;

        while ((rawLine = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(rawLine);
            }
            catch (JsonException)
            {
                problems.Add(new PiParseProblem(
                    LineNumber: lineNumber,
                    Code: MalformedLineCode,
                    Message: string.Create(CultureInfo.InvariantCulture, $"Skipping malformed Pi JSONL line {lineNumber}.")));
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new PiParseProblem(
                        LineNumber: lineNumber,
                        Code: MalformedLineCode,
                        Message: string.Create(CultureInfo.InvariantCulture, $"Skipping non-object Pi JSONL line {lineNumber}.")));
                    continue;
                }

                JsonElement root = document.RootElement;
                string entryType = ReadSafeString(root, "type") ?? string.Empty;
                string? id = ReadSafeString(root, "id");
                string? timestamp = ReadStringOnly(root, "timestamp");
                string? cwd = ReadSafeString(root, "cwd");
                string? model = ReadSafeString(root, "model");
                string? entryTitle = ReadSafeString(root, "title");

                if (string.Equals(entryType, "session", StringComparison.Ordinal))
                {
                    if (id is not null)
                    {
                        sessionId = id;
                    }

                    if (cwd is not null)
                    {
                        workingDirectory = cwd;
                    }
                }
                else if (string.Equals(entryType, "title", StringComparison.Ordinal) && entryTitle is not null)
                {
                    title = entryTitle;
                }

                PiMessage? message = null;
                if (string.Equals(entryType, "message", StringComparison.Ordinal)
                    && root.TryGetProperty("message", out JsonElement msgEl)
                    && msgEl.ValueKind == JsonValueKind.Object)
                {
                    message = ParseMessage(msgEl);
                }

                entries.Add(new PiEntry(
                    LineNumber: lineNumber,
                    EntryType: entryType,
                    Id: id,
                    Timestamp: timestamp,
                    WorkingDirectory: cwd,
                    Model: model,
                    Title: entryTitle,
                    Message: message));
            }
        }

        return new PiDocument(sessionId, workingDirectory, title, entries, problems);
    }

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        string harnessId = ResolveHarnessId(unit.Unit.HarnessId);
        CollectedSource source = new(harnessId, unit.Unit.Kind, unit.Unit.SourceKey);

        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        PiDocument document = Parse(unit.RawText, unit.Unit.SourceKey);
        string sessionId = ResolveSessionId(document.SessionId, unit.Unit.SourceKey);
        string traceId = $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}";

        MapState state = new()
        {
            WorkingDirectory = document.WorkingDirectory,
            SessionTitle = document.Title,
        };

        foreach (PiEntry entry in document.Entries)
        {
            ProcessEntry(entry, state, harnessId, sessionId, traceId);
        }

        List<CollectedSession> sessions = BuildSessions(state, document.Entries.Count, sessionId, harnessId);
        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId, harnessId, importedAtUtc);
        return new CollectedBatch(source, sessions, state.Records, problems);
    }

    private sealed class MapState
    {
        public string ResolvedModel { get; set; } = string.Empty;
        public string PendingUserMessage { get; set; } = string.Empty;
        public string? SessionTimestamp { get; set; }
        public string? PendingUserTimestamp { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? SessionTitle { get; set; }
        public List<CollectedRecord> Records { get; } = [];
        public HashSet<string> SeenAnchors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SeenRecordIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed record AssistantBlocks(
        List<CollectedContentPart> Parts,
        List<string> SkillsInvoked,
        List<(PiToolCall Call, string NormalizedToolName)> ToolCalls,
        string? CorrectionRule);

    private static void ProcessEntry(
        PiEntry entry,
        MapState state,
        string harnessId,
        string sessionId,
        string traceId)
    {
        if (string.Equals(entry.EntryType, "title", StringComparison.Ordinal))
        {
            if (SafeOptionalText(entry.Title) is string nextTitle)
            {
                state.SessionTitle = nextTitle;
            }

            return;
        }

        if (string.Equals(entry.EntryType, "session", StringComparison.Ordinal))
        {
            ApplySessionEntry(entry, state);
            return;
        }

        if (string.Equals(entry.EntryType, "model_change", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(entry.Model))
            {
                state.ResolvedModel = entry.Model.Trim();
            }

            return;
        }

        if (string.Equals(entry.EntryType, "message", StringComparison.Ordinal) && entry.Message is PiMessage message)
        {
            ProcessMessageEntry(entry, message, state, harnessId, sessionId, traceId);
        }
    }

    private static void ApplySessionEntry(PiEntry entry, MapState state)
    {
        if (!string.IsNullOrWhiteSpace(entry.Timestamp))
        {
            state.SessionTimestamp = entry.Timestamp;
        }

        if (SafeOptionalText(entry.WorkingDirectory) is string nextCwd)
        {
            state.WorkingDirectory = nextCwd;
        }
    }

    private static void ProcessMessageEntry(
        PiEntry entry,
        PiMessage msg,
        MapState state,
        string harnessId,
        string sessionId,
        string traceId)
    {
        if (string.Equals(msg.Role, "user", StringComparison.Ordinal))
        {
            ApplyUserMessage(entry, msg, state);
            return;
        }

        if (!string.Equals(msg.Role, "assistant", StringComparison.Ordinal) || msg.Usage is not PiUsage usage)
        {
            return;
        }

        long inputCount = usage.InputTokens ?? 0L;
        long outputCount = usage.OutputTokens ?? 0L;
        if (inputCount == 0L && outputCount == 0L)
        {
            return;
        }

        DateTimeOffset? startedAt = TryParseTimestamp(entry.Timestamp)
            ?? TryParseTimestamp(state.PendingUserTimestamp)
            ?? TryParseTimestamp(state.SessionTimestamp);
        if (!startedAt.HasValue)
        {
            return;
        }

        string anchor = SafeOptionalText(msg.ResponseId)
            ?? SafeOptionalText(entry.Id)
            ?? SafeOptionalText(entry.Timestamp)
            ?? string.Create(CultureInfo.InvariantCulture, $"line-{entry.LineNumber}");
        if (!state.SeenAnchors.Add(anchor))
        {
            return;
        }

        string parentRecordId = EnsureUniqueRecordId(
            state.SeenRecordIds,
            string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}:{anchor}"),
            entry.LineNumber);

        string model = ResolveMessageModel(msg.Model, state.ResolvedModel);
        AssistantBlocks blocks = BuildAssistantBlocks(msg.Blocks, model, state.PendingUserMessage);
        CollectedRecord parentRecord = BuildParentRecord(
            usage,
            blocks,
            harnessId,
            sessionId,
            traceId,
            parentRecordId,
            model,
            startedAt.Value);
        state.Records.Add(parentRecord);

        for (int t = 0; t < blocks.ToolCalls.Count; t++)
        {
            (PiToolCall toolCall, string normalizedToolName) = blocks.ToolCalls[t];
            state.Records.Add(BuildToolInvokeRecord(
                parentRecord,
                harnessId,
                sessionId,
                traceId,
                toolCall,
                normalizedToolName,
                t,
                state.SeenRecordIds));
        }

        state.PendingUserMessage = string.Empty;
    }

    private static void ApplyUserMessage(PiEntry entry, PiMessage msg, MapState state)
    {
        if (!string.IsNullOrWhiteSpace(entry.Timestamp))
        {
            state.PendingUserTimestamp = entry.Timestamp;
        }
        else if (msg.AttributionTimestampMs.HasValue)
        {
            state.PendingUserTimestamp = DateTimeOffset
                .FromUnixTimeMilliseconds(msg.AttributionTimestampMs.Value)
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        List<string> texts = [];
        foreach (PiContentBlock block in msg.Blocks)
        {
            if (string.Equals(block.BlockType, "text", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(block.Text))
            {
                texts.Add(block.Text);
            }
        }

        if (texts.Count > 0)
        {
            state.PendingUserMessage = string.Join(' ', texts);
        }
    }

    private static AssistantBlocks BuildAssistantBlocks(
        IReadOnlyList<PiContentBlock> blocks,
        string model,
        string pendingUserMessage)
    {
        List<CollectedContentPart> parts = [];
        int position = 0;
        string? correctionRule = null;

        if (!string.IsNullOrEmpty(pendingUserMessage))
        {
            CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(pendingUserMessage);
            if (check.Matched)
            {
                correctionRule = check.Rule;
            }

            parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.UserMessage, pendingUserMessage, "user", model));
        }

        List<string> skillsInvoked = [];
        HashSet<string> seenSkills = new(StringComparer.Ordinal);
        List<(PiToolCall Call, string NormalizedToolName)> toolCallsToEmit = [];

        foreach (PiContentBlock block in blocks)
        {
            if (string.Equals(block.BlockType, "text", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(block.Text))
            {
                parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ConversationHistory, block.Text, "assistant", model));
                continue;
            }

            if (string.Equals(block.BlockType, "toolCall", StringComparison.Ordinal)
                && block.ToolCall is not null)
            {
                string? skill = ExtractSkillLoadName(
                    block.ToolCall.Name,
                    block.ToolCall.Path,
                    block.ToolCall.FilePath);
                if (skill is not null)
                {
                    if (seenSkills.Add(skill))
                    {
                        skillsInvoked.Add(skill);
                    }

                    toolCallsToEmit.Add((block.ToolCall, "Skill"));
                }
                else
                {
                    toolCallsToEmit.Add((block.ToolCall, MapToolName(block.ToolCall.Name)));
                }
            }
        }

        return new AssistantBlocks(parts, skillsInvoked, toolCallsToEmit, correctionRule);
    }

    private static CollectedRecord BuildParentRecord(
        PiUsage usage,
        AssistantBlocks blocks,
        string harnessId,
        string sessionId,
        string traceId,
        string parentRecordId,
        string model,
        DateTimeOffset startedAt)
    {
        (IReadOnlyList<CollectedTokenCount> tokens, long? reportedInput, long? reportedOutput) = BuildTokens(usage);
        CollectedCost? reportedCost = usage.ReportedCostUsd is > 0 && double.IsFinite(usage.ReportedCostUsd.Value)
            ? new CollectedCost(usage.ReportedCostUsd.Value, "USD")
            : null;

        return new CollectedRecord(
            RecordId: parentRecordId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: $"{harnessId}:{model}",
            StartedAt: startedAt,
            Status: RecordStatus.Ok,
            SessionKey: sessionId,
            ParentRecordId: null,
            TraceId: traceId,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: tokens,
            ReportedCost: reportedCost,
            Parts: blocks.Parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: blocks.SkillsInvoked,
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(
                ErrorMessage: null,
                EditFailures: null,
                CorrectionRule: blocks.CorrectionRule,
                DeclaredContextWindow: null),
            ReportedInputTokens: reportedInput,
            ReportedOutputTokens: reportedOutput);
    }

    private static List<CollectedSession> BuildSessions(
        MapState state,
        int entryCount,
        string sessionId,
        string harnessId)
    {
        List<CollectedSession> sessions = [];
        if (state.Records.Count > 0 || entryCount > 0)
        {
            CollectedWorkspace? workspace = state.WorkingDirectory is not null
                ? new CollectedWorkspace(state.WorkingDirectory, null, null)
                : null;

            sessions.Add(new CollectedSession(
                SessionKey: sessionId,
                HarnessId: harnessId,
                NativeSessionId: sessionId,
                Workspace: workspace,
                Title: state.SessionTitle,
                ReportedUsage: []));
        }

        return sessions;
    }

    /// <summary>
    /// Classifies a Pi/OMP <c>read</c> tool invocation targeting a <c>SKILL.md</c> file or
    /// <c>skill://</c> URI and returns the extracted skill name, or <see langword="null"/> for normal reads.
    /// </summary>
    /// <param name="toolName">Raw tool name (<c>read</c>).</param>
    /// <param name="path">Optional <c>arguments.path</c> value.</param>
    /// <param name="filePath">Optional <c>arguments.file_path</c> fallback value.</param>
    /// <returns>The extracted skill name, or <see langword="null"/> when not a skill load.</returns>
    public static string? ExtractSkillLoadName(string? toolName, string? path, string? filePath)
    {
        if (!string.Equals(toolName, "read", StringComparison.Ordinal))
        {
            return null;
        }

        string? raw = path ?? filePath;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("skill://", StringComparison.Ordinal))
        {
            string rest = trimmed["skill://".Length..].TrimStart('/');
            int sep = rest.IndexOfAny(['/', '?', '#']);
            string first = (sep >= 0 ? rest[..sep] : rest).Trim();
            return first.Length > 0 ? first : null;
        }

        string[] segments = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[^1], "SKILL.md", StringComparison.Ordinal))
        {
            return null;
        }

        string parent = segments[^2].Trim();
        return parent.Length > 0 ? parent : null;
    }

    /// <summary>
    /// Maps a raw Pi/OMP tool name to its canonical display name.
    /// </summary>
    /// <param name="rawToolName">Raw tool name from the transcript.</param>
    /// <returns>The canonical tool name.</returns>
    public static string MapToolName(string rawToolName)
    {
        ArgumentNullException.ThrowIfNull(rawToolName);
        return ToolNameMap.TryGetValue(rawToolName, out string? mapped) ? mapped : rawToolName;
    }

    private static PiMessage ParseMessage(JsonElement msgEl)
    {
        return new PiMessage(
            Role: ReadSafeString(msgEl, "role"),
            Model: ReadSafeString(msgEl, "model"),
            ResponseId: ReadSafeString(msgEl, "responseId"),
            AttributionTimestampMs: ReadAttributionTimestamp(msgEl),
            Usage: ParseUsage(msgEl),
            Blocks: ParseContentBlocks(msgEl));
    }

    private static long? ReadAttributionTimestamp(JsonElement msgEl)
    {
        if (!msgEl.TryGetProperty("attribution", out JsonElement attrEl)
            || attrEl.ValueKind != JsonValueKind.Object
            || !attrEl.TryGetProperty("timestamp", out JsonElement tsEl)
            || tsEl.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (tsEl.TryGetInt64(out long tsInt))
        {
            return tsInt;
        }

        return tsEl.TryGetDouble(out double tsDouble) && double.IsFinite(tsDouble) ? (long)tsDouble : null;
    }

    private static PiUsage? ParseUsage(JsonElement msgEl)
    {
        if (!msgEl.TryGetProperty("usage", out JsonElement usageEl) || usageEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? reportedCostUsd = null;
        if (usageEl.TryGetProperty("cost", out JsonElement costEl)
            && costEl.ValueKind == JsonValueKind.Object
            && costEl.TryGetProperty("total", out JsonElement totalEl)
            && totalEl.ValueKind == JsonValueKind.Number
            && totalEl.TryGetDouble(out double totalVal)
            && double.IsFinite(totalVal))
        {
            reportedCostUsd = totalVal;
        }

        return new PiUsage(
            InputTokens: ReadOptionalNonNegativeLong(usageEl, "input"),
            OutputTokens: ReadOptionalNonNegativeLong(usageEl, "output"),
            CacheReadTokens: ReadOptionalNonNegativeLong(usageEl, "cacheRead"),
            CacheWriteTokens: ReadOptionalNonNegativeLong(usageEl, "cacheWrite"),
            ReportedCostUsd: reportedCostUsd);
    }

    private static List<PiContentBlock> ParseContentBlocks(JsonElement msgEl)
    {
        List<PiContentBlock> blocks = [];
        if (!msgEl.TryGetProperty("content", out JsonElement contentEl))
        {
            return blocks;
        }

        if (contentEl.ValueKind == JsonValueKind.String)
        {
            string? scalarText = contentEl.GetString();
            if (scalarText is not null)
            {
                blocks.Add(new PiContentBlock("text", scalarText, null));
            }

            return blocks;
        }

        if (contentEl.ValueKind != JsonValueKind.Array)
        {
            return blocks;
        }

        foreach (JsonElement blockEl in contentEl.EnumerateArray())
        {
            PiContentBlock? block = ParseContentBlock(blockEl);
            if (block is not null)
            {
                blocks.Add(block);
            }
        }

        return blocks;
    }

    private static PiContentBlock? ParseContentBlock(JsonElement blockEl)
    {
        if (blockEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? blockType = ReadSafeString(blockEl, "type");
        if (string.Equals(blockType, "text", StringComparison.Ordinal))
        {
            return new PiContentBlock("text", ReadStringOnly(blockEl, "text"), null);
        }

        if (!string.Equals(blockType, "toolCall", StringComparison.Ordinal))
        {
            return null;
        }

        string? name = ReadSafeString(blockEl, "name");
        if (name is null)
        {
            return null;
        }

        string? callId = ReadSafeString(blockEl, "id");
        string? rawInputText = null;
        string? path = null;
        string? filePath = null;
        string? command = null;

        if (blockEl.TryGetProperty("arguments", out JsonElement argsEl)
            && argsEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            rawInputText = argsEl.GetRawText();
            if (argsEl.ValueKind == JsonValueKind.Object)
            {
                path = ReadStringOnly(argsEl, "path");
                filePath = ReadStringOnly(argsEl, "file_path");
                command = ReadStringOnly(argsEl, "command");
            }
        }

        return new PiContentBlock(
            BlockType: "toolCall",
            Text: null,
            ToolCall: new PiToolCall(callId, name, rawInputText, path, filePath, command));
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parentRecord,
        string harnessId,
        string sessionId,
        string traceId,
        PiToolCall toolCall,
        string normalizedToolName,
        int toolIndex,
        HashSet<string> seenRecordIds)
    {
        string safeCallId = SafeOptionalText(toolCall.CallId)
            ?? string.Create(CultureInfo.InvariantCulture, $"call-{toolIndex}");
        string rawInput = toolCall.RawInputText ?? "{}";

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{safeCallId}:{normalizedToolName}:{rawInput}"));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];
        string childRecordId = EnsureUniqueRecordId(seenRecordIds, $"{parentRecord.RecordId}-t{toolHash}", toolIndex);

        CollectedToolContent? inputContent = toolCall.RawInputText is not null
            ? ToolContentMeasurer.Measure(toolCall.RawInputText, CanonSynth.MaxToolArgumentsBytes, parentRecord.Model)
                .ToToolContent(includeFingerprint: true)
            : null;

        CollectedToolCall collectedTool = new(
            CallId: safeCallId,
            ToolName: normalizedToolName,
            McpServerName: null,
            McpToolName: null,
            Input: inputContent,
            Result: null,
            Succeeded: null,
            Error: null,
            ExitCode: null);

        return new CollectedRecord(
            RecordId: childRecordId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: normalizedToolName,
            StartedAt: parentRecord.StartedAt,
            Status: RecordStatus.Unset,
            SessionKey: sessionId,
            ParentRecordId: parentRecord.RecordId,
            TraceId: traceId,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: collectedTool,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null));
    }

    private static (IReadOnlyList<CollectedTokenCount> Tokens, long? ReportedInput, long? ReportedOutput) BuildTokens(PiUsage usage)
    {
        List<CollectedTokenCount> tokens = [];
        long inputSum = 0L;
        bool hasInput = false;

        if (usage.InputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, usage.InputTokens.Value));
            inputSum += usage.InputTokens.Value;
            hasInput = true;
        }

        if (usage.CacheReadTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, usage.CacheReadTokens.Value));
            inputSum += usage.CacheReadTokens.Value;
            hasInput = true;
        }

        if (usage.CacheWriteTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, usage.CacheWriteTokens.Value));
            inputSum += usage.CacheWriteTokens.Value;
            hasInput = true;
        }

        if (usage.OutputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, usage.OutputTokens.Value));
        }

        return (tokens, hasInput ? inputSum : null, usage.OutputTokens);
    }

    private static string ResolveMessageModel(string? messageModel, string? resolvedModel)
    {
        string msgModel = SafeOptionalText(messageModel) ?? string.Empty;
        string sessionModel = SafeOptionalText(resolvedModel) ?? string.Empty;

        if (msgModel.Contains('/', StringComparison.Ordinal))
        {
            return msgModel;
        }

        if (sessionModel.Length > 0
            && (msgModel.Length == 0
                || string.Equals(sessionModel, msgModel, StringComparison.Ordinal)
                || sessionModel.EndsWith($"/{msgModel}", StringComparison.Ordinal)))
        {
            return sessionModel;
        }

        if (msgModel.Length > 0)
        {
            return msgModel;
        }

        if (sessionModel.Length > 0)
        {
            return sessionModel;
        }

        return DefaultModel;
    }

    private static string ResolveHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return PiHarnessId;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
        return string.Equals(normalized, OmpHarnessId, StringComparison.Ordinal) ? OmpHarnessId : PiHarnessId;
    }

    private static string ResolveSessionId(string? parsedSessionId, string sourceKey)
    {
        string? explicitId = SafeOptionalText(parsedSessionId);
        if (explicitId is not null)
        {
            return explicitId;
        }

        string? fileStem = SafeOptionalText(Path.GetFileNameWithoutExtension(sourceKey));
        if (fileStem is not null)
        {
            return fileStem;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return $"pi-{Convert.ToHexStringLower(digest)[..16]}";
    }

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator) =>
        HarnessJson.EnsureUniqueRecordId(seenRecordIds, candidate, disambiguator);

    private static DateTimeOffset? TryParseTimestamp(string? rawTimestamp) =>
        HarnessJson.TryParseTimestamp(rawTimestamp);

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<PiParseProblem> parseProblems,
        string sourceKey,
        string sessionId,
        string harnessId,
        DateTimeOffset importedAtUtc)
    {
        if (parseProblems.Count == 0)
        {
            return [];
        }

        string timestampUtc = importedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        List<CollectedProblem> problems = new(parseProblems.Count);

        foreach (PiParseProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"pi-malformed:{sourceKey}:line-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed Pi JSONL line.";

            problems.Add(new CollectedProblem(
                ProblemKey: problemKey,
                Severity: "warning",
                Code: parseProblem.Code,
                Message: message,
                SpanId: null,
                Location: location,
                SessionKey: sessionId,
                HarnessId: harnessId,
                TimestampUtc: timestampUtc,
                Stack: null));
        }

        return problems;
    }

    private static string? ReadSafeString(JsonElement obj, string key) =>
        HarnessJson.ReadSafeString(obj, key);

    private static string? ReadStringOnly(JsonElement obj, string key) =>
        HarnessJson.ReadStringOnly(obj, key);

    private static long? ReadOptionalNonNegativeLong(JsonElement obj, string key) =>
        HarnessJson.ReadOptionalNonNegativeLong(obj, key);

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);
}
