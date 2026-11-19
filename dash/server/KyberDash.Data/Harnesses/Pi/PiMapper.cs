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

        string resolvedModel = string.Empty;
        string pendingUserMessage = string.Empty;
        string? sessionTimestamp = null;
        string? pendingUserTimestamp = null;
        string? workingDirectory = document.WorkingDirectory;
        string? sessionTitle = document.Title;

        List<CollectedRecord> records = [];
        HashSet<string> seenAnchors = new(StringComparer.Ordinal);
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (PiEntry entry in document.Entries)
        {
            if (string.Equals(entry.EntryType, "title", StringComparison.Ordinal))
            {
                if (SafeOptionalText(entry.Title) is string nextTitle)
                {
                    sessionTitle = nextTitle;
                }

                continue;
            }

            if (string.Equals(entry.EntryType, "session", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(entry.Timestamp))
                {
                    sessionTimestamp = entry.Timestamp;
                }

                if (SafeOptionalText(entry.WorkingDirectory) is string nextCwd)
                {
                    workingDirectory = nextCwd;
                }

                continue;
            }

            if (string.Equals(entry.EntryType, "model_change", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(entry.Model))
                {
                    resolvedModel = entry.Model.Trim();
                }

                continue;
            }

            if (!string.Equals(entry.EntryType, "message", StringComparison.Ordinal) || entry.Message is null)
            {
                continue;
            }

            PiMessage msg = entry.Message;

            if (string.Equals(msg.Role, "user", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(entry.Timestamp))
                {
                    pendingUserTimestamp = entry.Timestamp;
                }
                else if (msg.AttributionTimestampMs.HasValue)
                {
                    pendingUserTimestamp = DateTimeOffset
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
                    pendingUserMessage = string.Join(' ', texts);
                }

                continue;
            }

            if (!string.Equals(msg.Role, "assistant", StringComparison.Ordinal) || msg.Usage is null)
            {
                continue;
            }

            long inputCount = msg.Usage.InputTokens ?? 0L;
            long outputCount = msg.Usage.OutputTokens ?? 0L;
            if (inputCount == 0L && outputCount == 0L)
            {
                continue;
            }

            DateTimeOffset? startedAt = TryParseTimestamp(entry.Timestamp)
                ?? TryParseTimestamp(pendingUserTimestamp)
                ?? TryParseTimestamp(sessionTimestamp);
            if (!startedAt.HasValue)
            {
                continue;
            }

            string anchor = SafeOptionalText(msg.ResponseId)
                ?? SafeOptionalText(entry.Id)
                ?? SafeOptionalText(entry.Timestamp)
                ?? string.Create(CultureInfo.InvariantCulture, $"line-{entry.LineNumber}");
            if (!seenAnchors.Add(anchor))
            {
                continue;
            }

            string parentRecordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}:{anchor}"),
                entry.LineNumber);

            string model = ResolveMessageModel(msg.Model, resolvedModel);

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

                parts.Add(BuildContentPart(position++, ContentBucket.UserMessage, pendingUserMessage, "user"));
            }

            List<string> skillsInvoked = [];
            HashSet<string> seenSkills = new(StringComparer.Ordinal);
            List<(PiToolCall Call, string NormalizedToolName)> toolCallsToEmit = [];

            foreach (PiContentBlock block in msg.Blocks)
            {
                if (string.Equals(block.BlockType, "text", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(block.Text))
                {
                    parts.Add(BuildContentPart(position++, ContentBucket.ConversationHistory, block.Text, "assistant"));
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

            (IReadOnlyList<CollectedTokenCount> tokens, long? reportedInput, long? reportedOutput) = BuildTokens(msg.Usage);
            CollectedCost? reportedCost = msg.Usage.ReportedCostUsd is > 0 && double.IsFinite(msg.Usage.ReportedCostUsd.Value)
                ? new CollectedCost(msg.Usage.ReportedCostUsd.Value, "USD")
                : null;

            CollectedRecord parentRecord = new(
                RecordId: parentRecordId,
                HarnessId: harnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Internal,
                Name: $"{harnessId}:{model}",
                StartedAt: startedAt.Value,
                Status: RecordStatus.Ok,
                SessionKey: sessionId,
                ParentRecordId: null,
                TraceId: traceId,
                Model: model,
                EndedAt: null,
                ReportedDurationMs: null,
                Tokens: tokens,
                ReportedCost: reportedCost,
                Parts: parts,
                Tool: null,
                Agent: null,
                SkillsInvoked: skillsInvoked,
                AgentInstanceId: null,
                ToolDefinitions: [],
                Signals: new CollectedRecordSignals(
                    ErrorMessage: null,
                    EditFailures: null,
                    CorrectionRule: correctionRule,
                    DeclaredContextWindow: null),
                ReportedInputTokens: reportedInput,
                ReportedOutputTokens: reportedOutput);

            records.Add(parentRecord);

            for (int t = 0; t < toolCallsToEmit.Count; t++)
            {
                (PiToolCall toolCall, string normalizedToolName) = toolCallsToEmit[t];
                records.Add(BuildToolInvokeRecord(
                    parentRecord,
                    harnessId,
                    sessionId,
                    traceId,
                    toolCall,
                    normalizedToolName,
                    t,
                    seenRecordIds));
            }

            pendingUserMessage = string.Empty;
        }

        List<CollectedSession> sessions = [];
        if (records.Count > 0 || document.Entries.Count > 0)
        {
            CollectedWorkspace? workspace = workingDirectory is not null
                ? new CollectedWorkspace(workingDirectory, null, null)
                : null;

            sessions.Add(new CollectedSession(
                SessionKey: sessionId,
                HarnessId: harnessId,
                NativeSessionId: sessionId,
                Workspace: workspace,
                Title: sessionTitle,
                ReportedUsage: []));
        }

        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId, harnessId, importedAtUtc);
        return new CollectedBatch(source, sessions, records, problems);
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
        string? role = ReadSafeString(msgEl, "role");
        string? model = ReadSafeString(msgEl, "model");
        string? responseId = ReadSafeString(msgEl, "responseId");

        long? attributionTimestampMs = null;
        if (msgEl.TryGetProperty("attribution", out JsonElement attrEl)
            && attrEl.ValueKind == JsonValueKind.Object
            && attrEl.TryGetProperty("timestamp", out JsonElement tsEl)
            && tsEl.ValueKind == JsonValueKind.Number)
        {
            if (tsEl.TryGetInt64(out long tsInt))
            {
                attributionTimestampMs = tsInt;
            }
            else if (tsEl.TryGetDouble(out double tsDouble) && double.IsFinite(tsDouble))
            {
                attributionTimestampMs = (long)tsDouble;
            }
        }

        PiUsage? usage = null;
        if (msgEl.TryGetProperty("usage", out JsonElement usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
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

            usage = new PiUsage(
                InputTokens: ReadOptionalNonNegativeLong(usageEl, "input"),
                OutputTokens: ReadOptionalNonNegativeLong(usageEl, "output"),
                CacheReadTokens: ReadOptionalNonNegativeLong(usageEl, "cacheRead"),
                CacheWriteTokens: ReadOptionalNonNegativeLong(usageEl, "cacheWrite"),
                ReportedCostUsd: reportedCostUsd);
        }

        List<PiContentBlock> blocks = [];
        if (msgEl.TryGetProperty("content", out JsonElement contentEl))
        {
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                string? scalarText = contentEl.GetString();
                if (scalarText is not null)
                {
                    blocks.Add(new PiContentBlock("text", scalarText, null));
                }
            }
            else if (contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement blockEl in contentEl.EnumerateArray())
                {
                    if (blockEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? blockType = ReadSafeString(blockEl, "type");
                    if (string.Equals(blockType, "text", StringComparison.Ordinal))
                    {
                        string? text = ReadStringOnly(blockEl, "text");
                        blocks.Add(new PiContentBlock("text", text, null));
                    }
                    else if (string.Equals(blockType, "toolCall", StringComparison.Ordinal))
                    {
                        string? name = ReadSafeString(blockEl, "name");
                        if (name is null)
                        {
                            continue;
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

                        blocks.Add(new PiContentBlock(
                            BlockType: "toolCall",
                            Text: null,
                            ToolCall: new PiToolCall(callId, name, rawInputText, path, filePath, command)));
                    }
                }
            }
        }

        return new PiMessage(role, model, responseId, attributionTimestampMs, usage, blocks);
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
            ? ToolContentMeasurer.Measure(toolCall.RawInputText, CanonSynth.MaxToolArgumentsBytes)
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
            Position: position,
            Bucket: bucket,
            PlainText: plainText,
            Withheld: withheld,
            ReportedTokens: null,
            Server: null,
            Role: role,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: originalBytes);
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

    private static DateTimeOffset? TryParseTimestamp(string? rawTimestamp)
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
        SafeOptionalText(ReadStringOnly(obj, key));

    private static string? ReadStringOnly(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return el.GetString();
    }

    private static long? ReadOptionalNonNegativeLong(JsonElement obj, string key)
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
