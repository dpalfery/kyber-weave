using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Droid;

/// <summary>
/// Typed representation of a Droid <c>tool_use</c> content block.
/// </summary>
/// <param name="Id">Optional native tool call identifier (<c>id</c>).</param>
/// <param name="Name">Raw native tool name (<c>name</c>).</param>
/// <param name="RawInputJson">Raw JSON input payload text (<c>input</c>), when present.</param>
public record DroidToolUse(
    string? Id,
    string Name,
    string? RawInputJson);

/// <summary>
/// Typed representation of a Droid <c>tool_result</c> content block.
/// </summary>
/// <param name="ToolUseId">Correlated tool call identifier (<c>tool_use_id</c>), when present.</param>
/// <param name="IsError">Whether the tool reported an error (<c>is_error</c>).</param>
/// <param name="ContentText">Result text payload, when present.</param>
public record DroidToolResult(
    string? ToolUseId,
    bool IsError,
    string? ContentText);

/// <summary>
/// Typed representation of one content block inside a Droid transcript message.
/// </summary>
/// <param name="BlockType">Content block type (<c>text</c>, <c>thinking</c>, <c>tool_use</c>, or <c>tool_result</c>).</param>
/// <param name="Text">Text payload for <c>text</c> blocks.</param>
/// <param name="Thinking">Reasoning payload for <c>thinking</c> blocks.</param>
/// <param name="ToolUse">Tool invocation payload for <c>tool_use</c> blocks.</param>
/// <param name="ToolResult">Tool outcome payload for <c>tool_result</c> blocks.</param>
public record DroidContentBlock(
    string BlockType,
    string? Text,
    string? Thinking,
    DroidToolUse? ToolUse,
    DroidToolResult? ToolResult);

/// <summary>
/// Typed representation of the <c>message</c> object on a Droid <c>message</c> JSONL entry.
/// </summary>
/// <param name="Role">Message role (<c>user</c> or <c>assistant</c>).</param>
/// <param name="Blocks">Normalized content blocks extracted from <c>message.content</c>.</param>
public record DroidMessage(
    string? Role,
    IReadOnlyList<DroidContentBlock> Blocks);

/// <summary>
/// Typed representation of one JSON object line in a Droid session JSONL file.
/// </summary>
/// <param name="LineNumber">1-based line number within the transcript file.</param>
/// <param name="EntryType">Top-level entry <c>type</c> (<c>session_start</c> or <c>message</c>).</param>
/// <param name="Id">Optional native entry identifier (<c>id</c>).</param>
/// <param name="Timestamp">Optional ISO-8601 UTC timestamp string (<c>timestamp</c>).</param>
/// <param name="Title">Optional session title (<c>title</c>) on <c>session_start</c> entries.</param>
/// <param name="WorkingDirectory">Optional workspace directory (<c>cwd</c>) on <c>session_start</c> entries.</param>
/// <param name="Message">Optional parsed <c>message</c> payload on <c>message</c> entries.</param>
public record DroidEntry(
    int LineNumber,
    string EntryType,
    string? Id,
    string? Timestamp,
    string? Title,
    string? WorkingDirectory,
    DroidMessage? Message);

/// <summary>
/// Session-level token usage counters from the companion <c>.settings.json</c> file.
/// </summary>
/// <param name="InputTokens">Exclusive input tokens (<c>tokenUsage.inputTokens</c>), when present.</param>
/// <param name="OutputTokens">Generated output tokens (<c>tokenUsage.outputTokens</c>), when present.</param>
/// <param name="CacheCreationTokens">Cache-creation tokens (<c>tokenUsage.cacheCreationTokens</c>), when present.</param>
/// <param name="CacheReadTokens">Cache-read tokens (<c>tokenUsage.cacheReadTokens</c>), when present.</param>
/// <param name="ThinkingTokens">Thinking tokens (<c>tokenUsage.thinkingTokens</c>), when present.</param>
public record DroidTokenUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CacheCreationTokens,
    long? CacheReadTokens,
    long? ThinkingTokens);

/// <summary>
/// Typed representation of the companion <c>.settings.json</c> file read alongside a session transcript.
/// </summary>
/// <param name="Model">Session model identifier (<c>model</c>), when present.</param>
/// <param name="TokenUsage">Session-level token usage counters (<c>tokenUsage</c>), when present.</param>
public record DroidSettings(
    string? Model,
    DroidTokenUsage? TokenUsage);

/// <summary>
/// Non-fatal parse problem encountered while reading a Droid JSONL line.
/// </summary>
/// <param name="LineNumber">1-based line number within the JSONL file.</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record DroidParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Parsed Droid session JSONL document produced by <see cref="DroidSessionMapper.Parse"/>.
/// </summary>
/// <param name="HasSessionStart">Whether the transcript contains a <c>session_start</c> entry.</param>
/// <param name="SessionId">Resolved native session identifier from <c>session_start.id</c>, or <see langword="null"/> when absent.</param>
/// <param name="Title">Session title from <c>session_start.title</c>, or <see langword="null"/> when absent.</param>
/// <param name="WorkingDirectory">Workspace directory from <c>session_start.cwd</c>, or <see langword="null"/> when absent.</param>
/// <param name="SessionTimestamp">Timestamp from <c>session_start.timestamp</c>, or <see langword="null"/> when absent.</param>
/// <param name="Entries">Ordered parsed entries from the JSONL file.</param>
/// <param name="ParseProblems">Non-fatal line-level parse problems encountered while reading.</param>
public record DroidDocument(
    bool HasSessionStart,
    string? SessionId,
    string? Title,
    string? WorkingDirectory,
    string? SessionTimestamp,
    IReadOnlyList<DroidEntry> Entries,
    IReadOnlyList<DroidParseProblem> ParseProblems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting <c>dash/src/providers/droid.ts</c> into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// Reads Factory Droid session transcripts (<c>~/.factory/sessions</c> JSONL) with their companion
/// <c>.settings.json</c> files (surfaced as <see cref="NativeSourceUnit.CompanionText"/>): tracks
/// <c>session_start</c> and <c>message</c> entries and ignores other Droid session entry types per
/// the Droid JSONL format; skips harness-internal housekeeping sessions whose working directory is
/// the Factory root itself; strips Droid model wrappers (<c>custom:</c> prefixes, bracket segments,
/// trailing numeric suffixes) for record model identity; distributes session-level token usage
/// evenly across emitted assistant calls with the remainder on the last call; pairs
/// <c>tool_result</c> blocks from user messages onto pending tool calls by <c>tool_use_id</c> to
/// set child durations, outcomes, and error details; and withholds JSON document content while
/// reporting malformed lines and settings as warnings. Cost is never persisted: Droid reports no
/// per-call cost and rate-table estimation happens at read/projection time.
/// </remarks>
public class DroidSessionMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for Droid transcripts.</summary>
    public const string DroidHarnessId = "droid";

    /// <summary>Diagnostic code emitted when a Droid JSONL line is malformed.</summary>
    public const string MalformedLineCode = "DROID_MALFORMED_LINE";

    /// <summary>Diagnostic code emitted when the companion settings file is malformed.</summary>
    public const string MalformedSettingsCode = "DROID_MALFORMED_SETTINGS";

    private const string UnknownModel = "unknown";
    private const int MaxUserMessageChars = 500;
    private const string SystemReminderPrefix = "<system-reminder>";
    private const string FallbackToolErrorMessage = "Tool execution failed";
    private const string McpPrefix = "mcp__";

    private static readonly IReadOnlyDictionary<string, string> ToolNameMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Read"] = "Read",
            ["Create"] = "Create",
            ["Edit"] = "Edit",
            ["MultiEdit"] = "MultiEdit",
            ["LS"] = "LS",
            ["Glob"] = "Glob",
            ["Grep"] = "Grep",
            ["Execute"] = "Bash",
            ["AskUser"] = "AskUser",
            ["TodoWrite"] = "TodoWrite",
            ["Skill"] = "Skill",
            ["Task"] = "Agent",
            ["WebSearch"] = "WebSearch",
            ["FetchUrl"] = "FetchUrl",
            ["GenerateDroid"] = "GenerateDroid",
            ["ExitSpecMode"] = "ExitSpecMode",
        };

    /// <inheritdoc />
    public string HarnessId => DroidHarnessId;

    /// <summary>
    /// Parses raw Droid session JSONL transcript text into a strongly typed <see cref="DroidDocument"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the session JSONL file.</param>
    /// <param name="sourceKey">Optional path locator reserved for diagnostics.</param>
    /// <returns>The parsed <see cref="DroidDocument"/>.</returns>
    public static DroidDocument Parse(string? rawText, string? sourceKey = null)
    {
        _ = sourceKey;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new DroidDocument(false, null, null, null, null, [], []);
        }

        List<DroidEntry> entries = [];
        List<DroidParseProblem> problems = [];
        bool hasSessionStart = false;
        string? sessionId = null;
        string? title = null;
        string? workingDirectory = null;
        string? sessionTimestamp = null;

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
                problems.Add(new DroidParseProblem(
                    LineNumber: lineNumber,
                    Code: MalformedLineCode,
                    Message: string.Create(CultureInfo.InvariantCulture, $"Skipping malformed Droid JSONL line {lineNumber}.")));
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new DroidParseProblem(
                        LineNumber: lineNumber,
                        Code: MalformedLineCode,
                        Message: string.Create(CultureInfo.InvariantCulture, $"Skipping non-object Droid JSONL line {lineNumber}.")));
                    continue;
                }

                JsonElement root = document.RootElement;
                string entryType = ReadSafeString(root, "type") ?? string.Empty;
                string? id = ReadSafeString(root, "id");
                string? timestamp = ReadStringOnly(root, "timestamp");
                string? entryTitle = ReadSafeString(root, "title");
                string? cwd = ReadSafeString(root, "cwd");

                if (string.Equals(entryType, "session_start", StringComparison.Ordinal))
                {
                    hasSessionStart = true;
                    if (id is not null)
                    {
                        sessionId = id;
                    }

                    if (entryTitle is not null)
                    {
                        title = entryTitle;
                    }

                    if (cwd is not null)
                    {
                        workingDirectory = cwd;
                    }

                    if (ReadSafeString(root, "timestamp") is string startTimestamp)
                    {
                        sessionTimestamp = startTimestamp;
                    }
                }

                DroidMessage? message = null;
                if (string.Equals(entryType, "message", StringComparison.Ordinal)
                    && root.TryGetProperty("message", out JsonElement msgEl)
                    && msgEl.ValueKind == JsonValueKind.Object)
                {
                    message = ParseMessage(msgEl);
                }

                entries.Add(new DroidEntry(
                    LineNumber: lineNumber,
                    EntryType: entryType,
                    Id: id,
                    Timestamp: timestamp,
                    Title: entryTitle,
                    WorkingDirectory: cwd,
                    Message: message));
            }
        }

        return new DroidDocument(hasSessionStart, sessionId, title, workingDirectory, sessionTimestamp, entries, problems);
    }

    /// <summary>
    /// Strips the Droid-specific model wrapper to get the model's display name.
    /// </summary>
    /// <param name="rawModel">Raw model identifier from the companion settings file.</param>
    /// <returns>The stripped model name, or <see langword="null"/> when nothing remains.</returns>
    public static string? StripModelPrefix(string rawModel)
    {
        ArgumentNullException.ThrowIfNull(rawModel);

        string stripped = rawModel;
        const string customPrefix = "custom:";
        if (stripped.StartsWith(customPrefix, StringComparison.Ordinal))
        {
            stripped = stripped[customPrefix.Length..];
        }

        stripped = RemoveBracketSegments(stripped);

        int lastDash = stripped.LastIndexOf('-');
        if (lastDash >= 0 && lastDash + 1 < stripped.Length)
        {
            bool allDigits = true;
            for (int i = lastDash + 1; i < stripped.Length; i++)
            {
                if (!char.IsAsciiDigit(stripped[i]))
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits)
            {
                stripped = stripped[..lastDash];
            }
        }

        stripped = stripped.Trim('-');

        return stripped.Length == 0 ? null : stripped;
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

        DroidDocument document = Parse(unit.RawText, unit.Unit.SourceKey);
        if (!document.HasSessionStart)
        {
            return new CollectedBatch(source, [], [], BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId: null, harnessId, importedAtUtc));
        }

        string sessionId = ResolveSessionId(document.SessionId, unit.Unit.SourceKey);
        if (IsInternalSession(document.WorkingDirectory, unit.Unit.SourceKey))
        {
            return new CollectedBatch(source, [], [], []);
        }

        (DroidSettings settings, DroidParseProblem? settingsProblem) = ParseSettings(unit.CompanionText);
        string? strippedModel = settings.Model is null ? null : StripModelPrefix(settings.Model);
        string displayModel = strippedModel ?? UnknownModel;

        List<PendingAssistantCall> calls = CollectAssistantCalls(document, importedAtUtc);

        List<DroidParseProblem> allProblems = [.. document.ParseProblems];
        if (settingsProblem is not null)
        {
            allProblems.Add(settingsProblem);
        }

        if (calls.Count == 0)
        {
            return new CollectedBatch(source, [], [], BuildProblems(allProblems, unit.Unit.SourceKey, sessionId, harnessId, importedAtUtc));
        }

        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}");

        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);
        int callCount = calls.Count;

        for (int i = 0; i < calls.Count; i++)
        {
            bool isLast = i == calls.Count - 1;
            PendingAssistantCall call = calls[i];
            records.AddRange(BuildParentRecord(
                call,
                harnessId,
                sessionId,
                traceId,
                strippedModel,
                displayModel,
                settings.TokenUsage,
                callCount,
                isLast,
                seenRecordIds));
        }

        CollectedWorkspace? workspace = SafeOptionalText(document.WorkingDirectory) is string cwd
            ? new CollectedWorkspace(cwd, null, null)
            : null;

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: sessionId,
                HarnessId: harnessId,
                NativeSessionId: sessionId,
                Workspace: workspace,
                Title: document.Title,
                ReportedUsage: BuildReportedUsage(displayModel, settings.TokenUsage)),
        ];

        List<CollectedProblem> problems = BuildProblems(allProblems, unit.Unit.SourceKey, sessionId, harnessId, importedAtUtc);
        return new CollectedBatch(source, sessions, records, problems);
    }

    private static List<PendingAssistantCall> CollectAssistantCalls(DroidDocument document, DateTimeOffset importedAtUtc)
    {
        List<PendingAssistantCall> calls = [];
        HashSet<string> seenAnchors = new(StringComparer.Ordinal);
        List<DroidToolUse> pendingTools = [];
        List<string> pendingThinking = [];
        string pendingUserMessage = string.Empty;
        DateTimeOffset? boundaryMs = null;
        DateTimeOffset? sessionTimestamp = TryParseTimestamp(document.SessionTimestamp);

        foreach (DroidEntry entry in document.Entries)
        {
            if (!string.Equals(entry.EntryType, "message", StringComparison.Ordinal) || entry.Message is null)
            {
                continue;
            }

            DroidMessage msg = entry.Message;
            DateTimeOffset? entryTimestamp = TryParseTimestamp(entry.Timestamp);

            if (string.Equals(msg.Role, "user", StringComparison.Ordinal))
            {
                List<string> texts = [];
                foreach (DroidContentBlock block in msg.Blocks)
                {
                    if (string.Equals(block.BlockType, "text", StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(block.Text))
                    {
                        texts.Add(block.Text);
                    }
                }

                List<string> visibleTexts = texts
                    .Where(static t => !t.StartsWith(SystemReminderPrefix, StringComparison.Ordinal))
                    .ToList();
                if (visibleTexts.Count > 0)
                {
                    pendingUserMessage = TruncateUserMessage(string.Join(' ', visibleTexts));
                    if (entryTimestamp.HasValue)
                    {
                        boundaryMs = entryTimestamp;
                    }
                }

                foreach (DroidContentBlock block in msg.Blocks)
                {
                    if (string.Equals(block.BlockType, "tool_result", StringComparison.Ordinal)
                        && block.ToolResult is not null)
                    {
                        PairToolResult(calls, block.ToolResult, entryTimestamp);
                    }
                }

                continue;
            }

            if (!string.Equals(msg.Role, "assistant", StringComparison.Ordinal))
            {
                continue;
            }

            List<string> assistantTexts = [];
            foreach (DroidContentBlock block in msg.Blocks)
            {
                if (string.Equals(block.BlockType, "text", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(block.Text))
                {
                    assistantTexts.Add(block.Text);
                }
                else if (string.Equals(block.BlockType, "thinking", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(block.Thinking))
                {
                    pendingThinking.Add(block.Thinking);
                }
                else if (string.Equals(block.BlockType, "tool_use", StringComparison.Ordinal)
                    && block.ToolUse is not null)
                {
                    pendingTools.Add(block.ToolUse);
                }
            }

            if (pendingTools.Count == 0 && assistantTexts.Count == 0)
            {
                continue;
            }

            string anchor = SafeOptionalText(entry.Id)
                ?? SafeOptionalText(entry.Timestamp)
                ?? string.Create(CultureInfo.InvariantCulture, $"line-{entry.LineNumber}");
            if (!seenAnchors.Add(anchor))
            {
                continue;
            }

            DateTimeOffset startedAt = entryTimestamp ?? sessionTimestamp ?? importedAtUtc;
            DateTimeOffset? boundary = boundaryMs ?? sessionTimestamp;
            long? activeDurationMs = null;
            if (boundary.HasValue)
            {
                double intervalMs = (startedAt - boundary.Value).TotalMilliseconds;
                if (intervalMs >= 0)
                {
                    activeDurationMs = (long)intervalMs;
                }
            }

            string callId = SafeOptionalText(entry.Id)
                ?? string.Create(CultureInfo.InvariantCulture, $"msg-{calls.Count}");

            string? correctionRule = null;
            if (!string.IsNullOrEmpty(pendingUserMessage))
            {
                CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(pendingUserMessage);
                if (check.Matched)
                {
                    correctionRule = check.Rule;
                }
            }

            List<PendingToolCall> tools = pendingTools
                .Select(static tool => new PendingToolCall(tool, MapToolName(tool.Name)))
                .ToList();

            calls.Add(new PendingAssistantCall(
                callId,
                startedAt,
                activeDurationMs,
                pendingUserMessage,
                correctionRule,
                [.. pendingThinking],
                [.. assistantTexts],
                tools));

            pendingTools.Clear();
            pendingThinking.Clear();
            pendingUserMessage = string.Empty;
            if (boundaryMs is null || startedAt >= boundaryMs)
            {
                boundaryMs = startedAt;
            }
        }

        return calls;
    }

    private static void PairToolResult(List<PendingAssistantCall> calls, DroidToolResult result, DateTimeOffset? resultTimestamp)
    {
        if (string.IsNullOrEmpty(result.ToolUseId))
        {
            return;
        }

        for (int i = calls.Count - 1; i >= 0; i--)
        {
            PendingAssistantCall call = calls[i];
            foreach (PendingToolCall tool in call.Tools)
            {
                if (string.Equals(tool.Use.Id, result.ToolUseId, StringComparison.Ordinal))
                {
                    tool.ResultTimestamp = resultTimestamp;
                    tool.IsError = result.IsError;
                    tool.ResultText = result.ContentText;
                    return;
                }
            }
        }
    }

    private static IReadOnlyList<CollectedRecord> BuildParentRecord(
        PendingAssistantCall call,
        string harnessId,
        string sessionId,
        string traceId,
        string? strippedModel,
        string displayModel,
        DroidTokenUsage? usage,
        int callCount,
        bool isLast,
        HashSet<string> seenRecordIds)
    {
        string recordId = EnsureUniqueRecordId(
            seenRecordIds,
            string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}:{call.CallId}"),
            callCount);

        List<CollectedContentPart> parts = [];
        int position = 0;
        parts.Add(BuildContentPart(position++, ContentBucket.UserMessage, call.UserMessage, "user"));
        foreach (string thinking in call.ThinkingTexts)
        {
            parts.Add(BuildContentPart(position++, ContentBucket.Reasoning, thinking, "assistant"));
        }

        foreach (string text in call.Texts)
        {
            parts.Add(BuildContentPart(position++, ContentBucket.ConversationHistory, text, "assistant"));
        }

        List<string> skillsInvoked = [];
        HashSet<string> seenSkills = new(StringComparer.Ordinal);
        foreach (PendingToolCall tool in call.Tools)
        {
            if (string.Equals(tool.NormalizedName, "Skill", StringComparison.Ordinal)
                && ExtractSkillName(tool.Use.RawInputJson) is string skill
                && seenSkills.Add(skill))
            {
                skillsInvoked.Add(skill);
            }
        }

        DateTimeOffset? endedAt = call.ActiveDurationMs.HasValue
            ? call.Timestamp.AddMilliseconds(call.ActiveDurationMs.Value)
            : null;

        CollectedRecord parentRecord = new(
            RecordId: recordId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: string.Create(CultureInfo.InvariantCulture, $"{harnessId}:{displayModel}"),
            StartedAt: call.Timestamp,
            Status: RecordStatus.Ok,
            SessionKey: sessionId,
            ParentRecordId: null,
            TraceId: traceId,
            Model: strippedModel,
            EndedAt: endedAt,
            ReportedDurationMs: call.ActiveDurationMs,
            Tokens: BuildRecordTokens(usage, callCount, isLast),
            ReportedCost: null,
            Parts: parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: skillsInvoked,
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, call.CorrectionRule, null));

        List<CollectedRecord> records = [parentRecord];
        for (int t = 0; t < call.Tools.Count; t++)
        {
            records.Add(BuildToolInvokeRecord(parentRecord, harnessId, sessionId, traceId, call.Tools[t], t, seenRecordIds));
        }

        return records;
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parentRecord,
        string harnessId,
        string sessionId,
        string traceId,
        PendingToolCall tool,
        int toolIndex,
        HashSet<string> seenRecordIds)
    {
        string safeCallId = SafeOptionalText(tool.Use.Id)
            ?? string.Create(CultureInfo.InvariantCulture, $"call-{toolIndex}");
        string rawInput = tool.Use.RawInputJson ?? "{}";

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{safeCallId}:{tool.NormalizedName}:{rawInput}"));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];
        string childRecordId = EnsureUniqueRecordId(seenRecordIds, $"{parentRecord.RecordId}-t{toolHash}", toolIndex);

        CollectedToolContent? inputContent = tool.Use.RawInputJson is not null
            ? ToolContentMeasurer.Measure(tool.Use.RawInputJson, CanonSynth.MaxToolArgumentsBytes)
                .ToToolContent(includeFingerprint: true)
            : null;

        string? resultText = SafeResultText(tool.ResultText);
        CollectedToolContent? resultContent = resultText is not null
            ? ToolContentMeasurer.Measure(resultText, CanonSynth.MaxToolResultBytes)
                .ToToolContent(includeFingerprint: false)
            : null;

        bool? succeeded = tool.IsError.HasValue ? !tool.IsError.Value : null;
        RecordStatus status = tool.IsError.HasValue
            ? (tool.IsError.Value ? RecordStatus.Error : RecordStatus.Ok)
            : RecordStatus.Unset;

        CollectedError? error = tool.IsError == true
            ? new CollectedError(!string.IsNullOrEmpty(resultContent?.PlainText) ? resultContent.PlainText : FallbackToolErrorMessage, null)
            : null;

        long? durationMs = null;
        if (tool.ResultTimestamp.HasValue)
        {
            double intervalMs = (tool.ResultTimestamp.Value - parentRecord.StartedAt).TotalMilliseconds;
            if (intervalMs >= 0)
            {
                durationMs = (long)intervalMs;
            }
        }

        DateTimeOffset? endedAt = durationMs.HasValue
            ? parentRecord.StartedAt.AddMilliseconds(durationMs.Value)
            : null;

        (string? mcpServerName, string? mcpToolName) = ParseMcpToolNames(tool.Use.Name);

        CollectedToolCall collectedTool = new(
            CallId: safeCallId,
            ToolName: tool.NormalizedName,
            McpServerName: mcpServerName,
            McpToolName: mcpToolName,
            Input: inputContent,
            Result: resultContent,
            Succeeded: succeeded,
            Error: error,
            ExitCode: null);

        List<CollectedContentPart> parts = [];
        if (resultText is not null)
        {
            parts.Add(BuildContentPart(0, ContentBucket.ToolResultContent, resultText, "user"));
        }

        return new CollectedRecord(
            RecordId: childRecordId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: tool.NormalizedName,
            StartedAt: parentRecord.StartedAt,
            Status: status,
            SessionKey: sessionId,
            ParentRecordId: parentRecord.RecordId,
            TraceId: traceId,
            Model: null,
            EndedAt: endedAt,
            ReportedDurationMs: durationMs,
            Tokens: [],
            ReportedCost: null,
            Parts: parts,
            Tool: collectedTool,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null));
    }

    private static IReadOnlyList<CollectedTokenCount> BuildRecordTokens(DroidTokenUsage? usage, int callCount, bool isLast)
    {
        if (usage is null || callCount <= 0)
        {
            return [];
        }

        List<CollectedTokenCount> tokens = [];
        AddDistributedTokens(tokens, TokenClass.Input, usage.InputTokens, callCount, isLast);
        AddDistributedTokens(tokens, TokenClass.Output, usage.OutputTokens, callCount, isLast);
        AddDistributedTokens(tokens, TokenClass.CacheWrite, usage.CacheCreationTokens, callCount, isLast);
        AddDistributedTokens(tokens, TokenClass.CacheRead, usage.CacheReadTokens, callCount, isLast);
        AddDistributedTokens(tokens, TokenClass.Reasoning, usage.ThinkingTokens, callCount, isLast);
        return tokens;
    }

    private static void AddDistributedTokens(List<CollectedTokenCount> tokens, TokenClass tokenClass, long? total, int callCount, bool isLast)
    {
        if (!total.HasValue)
        {
            return;
        }

        long perCall = total.Value / callCount;
        long count = isLast ? total.Value - (perCall * (callCount - 1)) : perCall;
        tokens.Add(new CollectedTokenCount(tokenClass, count));
    }

    private static IReadOnlyList<CollectedModelUsage> BuildReportedUsage(string displayModel, DroidTokenUsage? usage)
    {
        if (usage is null)
        {
            return [];
        }

        List<CollectedModelUsage> snapshots = [];
        AddUsageSnapshot(snapshots, displayModel, TokenClass.Input, usage.InputTokens);
        AddUsageSnapshot(snapshots, displayModel, TokenClass.Output, usage.OutputTokens);
        AddUsageSnapshot(snapshots, displayModel, TokenClass.CacheWrite, usage.CacheCreationTokens);
        AddUsageSnapshot(snapshots, displayModel, TokenClass.CacheRead, usage.CacheReadTokens);
        AddUsageSnapshot(snapshots, displayModel, TokenClass.Reasoning, usage.ThinkingTokens);
        return snapshots;
    }

    private static void AddUsageSnapshot(List<CollectedModelUsage> snapshots, string displayModel, TokenClass tokenClass, long? count)
    {
        if (count.HasValue)
        {
            snapshots.Add(new CollectedModelUsage(0, displayModel, tokenClass, count.Value));
        }
    }

    private static DroidMessage ParseMessage(JsonElement msgEl)
    {
        string? role = ReadSafeString(msgEl, "role");
        List<DroidContentBlock> blocks = [];

        if (!msgEl.TryGetProperty("content", out JsonElement contentEl))
        {
            return new DroidMessage(role, blocks);
        }

        if (contentEl.ValueKind == JsonValueKind.String)
        {
            string? scalarText = contentEl.GetString();
            if (scalarText is not null)
            {
                blocks.Add(new DroidContentBlock("text", scalarText, null, null, null));
            }

            return new DroidMessage(role, blocks);
        }

        if (contentEl.ValueKind != JsonValueKind.Array)
        {
            return new DroidMessage(role, blocks);
        }

        foreach (JsonElement blockEl in contentEl.EnumerateArray())
        {
            if (blockEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? blockType = ReadSafeString(blockEl, "type");
            if (string.Equals(blockType, "text", StringComparison.Ordinal))
            {
                blocks.Add(new DroidContentBlock("text", ReadStringOnly(blockEl, "text"), null, null, null));
            }
            else if (string.Equals(blockType, "thinking", StringComparison.Ordinal))
            {
                string? thinking = ReadStringOnly(blockEl, "thinking") ?? ReadStringOnly(blockEl, "text");
                blocks.Add(new DroidContentBlock("thinking", null, thinking, null, null));
            }
            else if (string.Equals(blockType, "tool_use", StringComparison.Ordinal))
            {
                string? name = ReadSafeString(blockEl, "name");
                if (name is null)
                {
                    continue;
                }

                string? rawInputJson = null;
                if (blockEl.TryGetProperty("input", out JsonElement inputEl)
                    && inputEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    rawInputJson = inputEl.GetRawText();
                }

                blocks.Add(new DroidContentBlock(
                    BlockType: "tool_use",
                    Text: null,
                    Thinking: null,
                    ToolUse: new DroidToolUse(ReadSafeString(blockEl, "id"), name, rawInputJson),
                    ToolResult: null));
            }
            else if (string.Equals(blockType, "tool_result", StringComparison.Ordinal))
            {
                bool isError = blockEl.TryGetProperty("is_error", out JsonElement errorEl)
                    && errorEl.ValueKind == JsonValueKind.True;
                blocks.Add(new DroidContentBlock(
                    BlockType: "tool_result",
                    Text: null,
                    Thinking: null,
                    ToolUse: null,
                    ToolResult: new DroidToolResult(
                        ReadSafeString(blockEl, "tool_use_id"),
                        isError,
                        ReadToolResultText(blockEl))));
            }
        }

        return new DroidMessage(role, blocks);
    }

    private static string? ReadToolResultText(JsonElement blockEl)
    {
        if (!blockEl.TryGetProperty("content", out JsonElement contentEl))
        {
            return null;
        }

        if (contentEl.ValueKind == JsonValueKind.String)
        {
            return contentEl.GetString();
        }

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            List<string> texts = [];
            foreach (JsonElement itemEl in contentEl.EnumerateArray())
            {
                if (itemEl.ValueKind == JsonValueKind.String && itemEl.GetString() is string itemText)
                {
                    texts.Add(itemText);
                }
                else if (itemEl.ValueKind == JsonValueKind.Object
                    && ReadStringOnly(itemEl, "text") is string nestedText)
                {
                    texts.Add(nestedText);
                }
            }

            return texts.Count > 0 ? string.Join(' ', texts) : null;
        }

        return null;
    }

    private static (DroidSettings Settings, DroidParseProblem? Problem) ParseSettings(string? companionText)
    {
        if (string.IsNullOrWhiteSpace(companionText))
        {
            return (new DroidSettings(null, null), null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(companionText);
        }
        catch (JsonException)
        {
            return (new DroidSettings(null, null), MalformedSettingsProblem());
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (new DroidSettings(null, null), MalformedSettingsProblem());
            }

            JsonElement root = document.RootElement;
            string? model = ReadSafeString(root, "model");

            DroidTokenUsage? usage = null;
            if (root.TryGetProperty("tokenUsage", out JsonElement usageEl)
                && usageEl.ValueKind == JsonValueKind.Object)
            {
                usage = new DroidTokenUsage(
                    InputTokens: ReadOptionalNonNegativeLong(usageEl, "inputTokens"),
                    OutputTokens: ReadOptionalNonNegativeLong(usageEl, "outputTokens"),
                    CacheCreationTokens: ReadOptionalNonNegativeLong(usageEl, "cacheCreationTokens"),
                    CacheReadTokens: ReadOptionalNonNegativeLong(usageEl, "cacheReadTokens"),
                    ThinkingTokens: ReadOptionalNonNegativeLong(usageEl, "thinkingTokens"));
            }

            return (new DroidSettings(model, usage), null);
        }
    }

    private static DroidParseProblem MalformedSettingsProblem() =>
        new(0, MalformedSettingsCode, "Skipping malformed Droid companion settings file.");

    /// <summary>
    /// Maps a raw Droid tool name to its canonical display name.
    /// </summary>
    /// <param name="rawToolName">Raw tool name from the transcript.</param>
    /// <returns>The canonical tool name.</returns>
    public static string MapToolName(string rawToolName)
    {
        ArgumentNullException.ThrowIfNull(rawToolName);
        return ToolNameMap.TryGetValue(rawToolName, out string? mapped) ? mapped : rawToolName;
    }

    /// <summary>
    /// Extracts the invoked skill name from a Droid <c>Skill</c> tool input payload.
    /// </summary>
    /// <param name="rawInputJson">Raw JSON input payload text.</param>
    /// <returns>The skill name, or <see langword="null"/> when the payload names none.</returns>
    public static string? ExtractSkillName(string? rawInputJson)
    {
        if (string.IsNullOrWhiteSpace(rawInputJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(rawInputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ReadSafeString(document.RootElement, "skill");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? ServerName, string? ToolName) ParseMcpToolNames(string toolName)
    {
        if (!toolName.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            return (null, null);
        }

        int separator = toolName.IndexOf("__", McpPrefix.Length, StringComparison.Ordinal);
        if (separator <= McpPrefix.Length || separator + 2 >= toolName.Length)
        {
            return (null, null);
        }

        string server = toolName[McpPrefix.Length..separator];
        string method = toolName[(separator + 2)..];
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(method)
            || JsonDocumentText.IsDocument(server) || JsonDocumentText.IsDocument(method))
        {
            return (null, null);
        }

        return (server, method);
    }

    private static CollectedContentPart BuildContentPart(
        int position,
        ContentBucket bucket,
        string rawText,
        string role)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(rawText, CanonSynth.MaxToolResultBytes);
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

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<DroidParseProblem> parseProblems,
        string sourceKey,
        string? sessionId,
        string harnessId,
        DateTimeOffset importedAtUtc)
    {
        if (parseProblems.Count == 0)
        {
            return [];
        }

        string timestampUtc = importedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        List<CollectedProblem> problems = new(parseProblems.Count);

        foreach (DroidParseProblem parseProblem in parseProblems)
        {
            bool isLineProblem = string.Equals(parseProblem.Code, MalformedLineCode, StringComparison.Ordinal);
            string problemKey = isLineProblem
                ? string.Create(CultureInfo.InvariantCulture, $"droid-malformed:{sourceKey}:line-{parseProblem.LineNumber}")
                : string.Create(CultureInfo.InvariantCulture, $"droid-malformed-settings:{sourceKey}");
            string location = isLineProblem
                ? string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}")
                : sourceKey;
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed Droid JSONL line.";

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

    private static string ResolveHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return DroidHarnessId;
        }

        return HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
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
        return $"droid-{Convert.ToHexStringLower(digest)[..16]}";
    }

    private static bool IsInternalSession(string? workingDirectory, string sourceKey)
    {
        string? cwd = SafeOptionalText(workingDirectory);
        if (cwd is null)
        {
            return false;
        }

        string normalizedCwd = cwd.Trim().TrimEnd('/', '\\');
        string? factoryDir = ResolveFactoryDir(sourceKey);
        return factoryDir is not null
            && string.Equals(normalizedCwd, factoryDir, StringComparison.Ordinal);
    }

    private static string? ResolveFactoryDir(string sourceKey)
    {
        // Session transcripts live at <factoryDir>/sessions/<dir>/<file>.jsonl, so the
        // Factory root is three segments above the transcript file.
        string[] segments = sourceKey.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4)
        {
            return null;
        }

        string factoryDir = string.Join('/', segments[..^3]);
        bool rooted = sourceKey.StartsWith('/') || sourceKey.StartsWith('\\');
        return rooted ? $"/{factoryDir}" : factoryDir;
    }

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator) =>
        HarnessJson.EnsureUniqueRecordId(seenRecordIds, candidate, disambiguator);

    private static DateTimeOffset? TryParseTimestamp(string? rawTimestamp) =>
        HarnessJson.TryParseTimestamp(rawTimestamp);

    private static string TruncateUserMessage(string message) =>
        message.Length > MaxUserMessageChars ? message[..MaxUserMessageChars] : message;

    private static string RemoveBracketSegments(string value)
    {
        StringBuilder builder = new(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            int open = value.IndexOf('[', i);
            if (open < 0)
            {
                builder.Append(value, i, value.Length - i);
                break;
            }

            builder.Append(value, i, open - i);
            int close = value.IndexOf(']', open + 1);
            i = close < 0 ? value.Length : close + 1;
        }

        return builder.ToString();
    }

    private static string? SafeResultText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }

    private static string? ReadSafeString(JsonElement obj, string key) =>
        HarnessJson.ReadSafeString(obj, key);

    private static string? ReadStringOnly(JsonElement obj, string key) =>
        HarnessJson.ReadStringOnly(obj, key);

    private static long? ReadOptionalNonNegativeLong(JsonElement obj, string key) =>
        HarnessJson.ReadOptionalNonNegativeLong(obj, key);

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);

    private sealed class PendingToolCall
    {
        internal PendingToolCall(DroidToolUse use, string normalizedName)
        {
            Use = use;
            NormalizedName = normalizedName;
        }

        internal DroidToolUse Use { get; }

        internal string NormalizedName { get; }

        internal DateTimeOffset? ResultTimestamp { get; set; }

        internal bool? IsError { get; set; }

        internal string? ResultText { get; set; }
    }

    private sealed class PendingAssistantCall
    {
        internal PendingAssistantCall(
            string callId,
            DateTimeOffset timestamp,
            long? activeDurationMs,
            string userMessage,
            string? correctionRule,
            List<string> thinkingTexts,
            List<string> texts,
            List<PendingToolCall> tools)
        {
            CallId = callId;
            Timestamp = timestamp;
            ActiveDurationMs = activeDurationMs;
            UserMessage = userMessage;
            CorrectionRule = correctionRule;
            ThinkingTexts = thinkingTexts;
            Texts = texts;
            Tools = tools;
        }

        internal string CallId { get; }

        internal DateTimeOffset Timestamp { get; }

        internal long? ActiveDurationMs { get; }

        internal string UserMessage { get; }

        internal string? CorrectionRule { get; }

        internal List<string> ThinkingTexts { get; }

        internal List<string> Texts { get; }

        internal List<PendingToolCall> Tools { get; }
    }
}
