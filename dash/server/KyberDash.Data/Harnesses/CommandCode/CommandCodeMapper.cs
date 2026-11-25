using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.CommandCode;

/// <summary>
/// Tool invocation extracted from a <c>tool_use</c> content block in a CommandCode assistant message.
/// </summary>
/// <param name="Name">Raw native tool name (<c>name</c>).</param>
/// <param name="RawInputText">Optional raw JSON text of the block <c>input</c> object.</param>
/// <param name="SkillName">Optional skill name from <c>input.name</c> on <c>activate_skill</c> calls.</param>
/// <param name="SubagentType">Optional subagent type from <c>input.subagent_type</c> on <c>agent</c> calls.</param>
/// <param name="CommandText">Optional shell command from <c>input.command</c> on <c>shell_command</c> calls.</param>
public record CommandCodeToolCall(
    string Name,
    string? RawInputText,
    string? SkillName,
    string? SubagentType,
    string? CommandText);

/// <summary>
/// Normalized content block inside a CommandCode transcript message.
/// </summary>
/// <param name="BlockType">Content block type (<c>text</c> or <c>tool_use</c>).</param>
/// <param name="Text">Text payload for <c>text</c> blocks or string <c>content</c>.</param>
/// <param name="ToolCall">Tool call payload for <c>tool_use</c> blocks.</param>
public record CommandCodeContentBlock(
    string BlockType,
    string? Text,
    CommandCodeToolCall? ToolCall);

/// <summary>
/// Token usage and optional cost counters carried on a CommandCode assistant <c>message</c> entry.
/// </summary>
/// <remarks>
/// Unlike Pi, usage sits on the entry rather than the message, and <c>inputTokens</c> follows the
/// AI SDK <c>totalUsage</c> convention of including both cache classes, so fresh input is recovered
/// by subtracting them before the call is yielded.
/// </remarks>
/// <param name="InputTokens">Total input tokens including cache classes (<c>usage.inputTokens</c>), when present.</param>
/// <param name="OutputTokens">Generated output tokens (<c>usage.outputTokens</c>), when present.</param>
/// <param name="CacheReadTokens">Cache-read tokens (<c>usage.cacheReadTokens</c>), when present.</param>
/// <param name="CacheWriteTokens">Cache-write tokens (<c>usage.cacheWriteTokens</c>), when present.</param>
/// <param name="ReportedCostUsd">Reported total USD cost (<c>usage.costUsd</c>), when present.</param>
public record CommandCodeUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    double? ReportedCostUsd);

/// <summary>
/// Typed representation of the <c>message</c> object on a CommandCode <c>message</c> JSONL entry.
/// </summary>
/// <param name="Role">Message role (<c>user</c> or <c>assistant</c>).</param>
/// <param name="Model">Optional per-message model identifier (<c>message-level model is carried on the entry</c>).</param>
/// <param name="Blocks">Normalized content blocks extracted from <c>message.content</c>.</param>
public record CommandCodeMessage(
    string? Role,
    string? Model,
    IReadOnlyList<CommandCodeContentBlock> Blocks);

/// <summary>
/// Typed representation of one JSON object line in a CommandCode session JSONL file.
/// </summary>
/// <param name="LineNumber">1-based line number within the transcript file.</param>
/// <param name="EntryType">Top-level entry <c>type</c> (<c>session</c>, <c>model_change</c>, <c>message</c>, etc.).</param>
/// <param name="Id">Optional native entry identifier (<c>id</c>).</param>
/// <param name="Timestamp">Optional ISO-8601 UTC timestamp string (<c>timestamp</c>).</param>
/// <param name="WorkingDirectory">Optional workspace directory (<c>cwd</c>) on <c>session</c> entries.</param>
/// <param name="Model">Optional model identifier (<c>model</c>) on <c>model_change</c> or <c>message</c> entries.</param>
/// <param name="Message">Optional parsed <c>message</c> payload on <c>message</c> entries.</param>
/// <param name="Usage">Optional usage counters on assistant <c>message</c> entries.</param>
public record CommandCodeEntry(
    int LineNumber,
    string EntryType,
    string? Id,
    string? Timestamp,
    string? WorkingDirectory,
    string? Model,
    CommandCodeMessage? Message,
    CommandCodeUsage? Usage);

/// <summary>
/// Non-fatal parse problem encountered while reading a CommandCode JSONL line.
/// </summary>
/// <param name="LineNumber">1-based line number within the JSONL file.</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record CommandCodeParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Parsed CommandCode session JSONL document produced by <see cref="CommandCodeMapper.Parse"/>.
/// </summary>
/// <param name="SessionId">Resolved native session identifier from <c>session.id</c>, or <see langword="null"/> when absent.</param>
/// <param name="WorkingDirectory">Workspace directory from <c>session.cwd</c>, or <see langword="null"/> when absent.</param>
/// <param name="Entries">Ordered parsed entries from the JSONL file.</param>
/// <param name="ParseProblems">Non-fatal line-level parse problems encountered while reading.</param>
public record CommandCodeDocument(
    string? SessionId,
    string? WorkingDirectory,
    IReadOnlyList<CommandCodeEntry> Entries,
    IReadOnlyList<CommandCodeParseProblem> ParseProblems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting <c>dash/src/providers/commandcode.ts</c> into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// Reads <c>~/.commandcode/projects/&lt;slug&gt;/&lt;session-id&gt;.jsonl</c> transcripts sharing Pi's
/// tree shape: a <c>session</c> header plus <c>model_change</c> and <c>message</c> entries. Every
/// assistant <c>message</c> entry carrying <c>usage</c> with a non-zero token class emits one
/// usage-bearing <c>llm.invoke</c> record (<c>RecordKind.Internal</c>) with fresh input recovered as
/// <c>inputTokens - cacheReadTokens - cacheWriteTokens</c>; each <c>tool_use</c> block emits a child
/// <c>tool.invoke</c> span. Zero-token, usage-less, and timestampless assistant messages are skipped,
/// and a zero <c>costUsd</c> is treated as absent so published rates apply at read time.
/// </remarks>
public class CommandCodeMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for CommandCode transcripts.</summary>
    public const string CommandCodeHarnessId = "commandcode";

    /// <summary>Diagnostic code emitted when a CommandCode JSONL line is malformed.</summary>
    public const string MalformedLineCode = "COMMANDCODE_MALFORMED_LINE";

    private static readonly IReadOnlyDictionary<string, string> ToolNameMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read_file"] = "Read",
            ["read_directory"] = "LS",
            ["write_file"] = "Write",
            ["edit_file"] = "Edit",
            ["glob"] = "Glob",
            ["grep"] = "Grep",
            ["shell_command"] = "Bash",
            ["web_search"] = "WebSearch",
            ["web_fetch"] = "WebFetch",
            ["todo_write"] = "TodoWrite",
            ["agent"] = "Agent",
            ["enter_plan_mode"] = "EnterPlanMode",
            ["exit_plan_mode"] = "ExitPlanMode",
        };

    /// <inheritdoc />
    public string HarnessId => CommandCodeHarnessId;

    /// <summary>
    /// Parses raw CommandCode JSONL transcript text into a strongly typed <see cref="CommandCodeDocument"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the session JSONL file.</param>
    /// <param name="sourceKey">Optional path locator reserved for diagnostics.</param>
    /// <returns>The parsed <see cref="CommandCodeDocument"/>.</returns>
    public static CommandCodeDocument Parse(string? rawText, string? sourceKey = null)
    {
        _ = sourceKey;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new CommandCodeDocument(null, null, [], []);
        }

        List<CommandCodeEntry> entries = [];
        List<CommandCodeParseProblem> problems = [];
        string? sessionId = null;
        string? workingDirectory = null;

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
                problems.Add(new CommandCodeParseProblem(
                    LineNumber: lineNumber,
                    Code: MalformedLineCode,
                    Message: string.Create(CultureInfo.InvariantCulture, $"Skipping malformed CommandCode JSONL line {lineNumber}.")));
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new CommandCodeParseProblem(
                        LineNumber: lineNumber,
                        Code: MalformedLineCode,
                        Message: string.Create(CultureInfo.InvariantCulture, $"Skipping non-object CommandCode JSONL line {lineNumber}.")));
                    continue;
                }

                JsonElement root = document.RootElement;
                string entryType = ReadSafeString(root, "type") ?? string.Empty;
                string? id = ReadSafeString(root, "id");
                string? timestamp = ReadStringOnly(root, "timestamp");
                string? cwd = ReadSafeString(root, "cwd");
                string? model = ReadSafeString(root, "model");

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

                CommandCodeMessage? message = null;
                CommandCodeUsage? usage = null;
                if (string.Equals(entryType, "message", StringComparison.Ordinal))
                {
                    if (root.TryGetProperty("message", out JsonElement msgEl)
                        && msgEl.ValueKind == JsonValueKind.Object)
                    {
                        message = ParseMessage(msgEl);
                    }

                    if (root.TryGetProperty("usage", out JsonElement usageEl)
                        && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        usage = ParseUsage(usageEl);
                    }
                }

                entries.Add(new CommandCodeEntry(
                    LineNumber: lineNumber,
                    EntryType: entryType,
                    Id: id,
                    Timestamp: timestamp,
                    WorkingDirectory: cwd,
                    Model: model,
                    Message: message,
                    Usage: usage));
            }
        }

        return new CommandCodeDocument(sessionId, workingDirectory, entries, problems);
    }

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        CollectedSource source = new(CommandCodeHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);

        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        CommandCodeDocument document = Parse(unit.RawText, unit.Unit.SourceKey);
        string sessionId = ResolveSessionId(document.SessionId, unit.Unit.SourceKey);
        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{CommandCodeHarnessId}:{sessionId}");

        string resolvedModel = string.Empty;
        string pendingUserMessage = string.Empty;
        string? sessionTimestamp = null;
        string? workingDirectory = document.WorkingDirectory;

        List<CollectedRecord> records = [];
        HashSet<string> seenAnchors = new(StringComparer.Ordinal);
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (CommandCodeEntry entry in document.Entries)
        {
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

            CommandCodeMessage msg = entry.Message;

            if (string.Equals(msg.Role, "user", StringComparison.Ordinal))
            {
                List<string> texts = [];
                foreach (CommandCodeContentBlock block in msg.Blocks)
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

            if (!string.Equals(msg.Role, "assistant", StringComparison.Ordinal) || entry.Usage is null)
            {
                continue;
            }

            CommandCodeUsage usage = entry.Usage;
            long cacheRead = usage.CacheReadTokens ?? 0L;
            long cacheWrite = usage.CacheWriteTokens ?? 0L;
            long input = Math.Max(0L, (usage.InputTokens ?? 0L) - cacheRead - cacheWrite);
            long output = usage.OutputTokens ?? 0L;
            if (input == 0L && output == 0L && cacheRead == 0L && cacheWrite == 0L)
            {
                continue;
            }

            DateTimeOffset? startedAt = TryParseTimestamp(entry.Timestamp)
                ?? TryParseTimestamp(sessionTimestamp);
            if (!startedAt.HasValue)
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

            string parentRecordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{CommandCodeHarnessId}:{sessionId}:{anchor}"),
                entry.LineNumber);

            string model = ResolveMessageModel(msg.Model, entry.Model, resolvedModel);

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

            foreach (CommandCodeContentBlock block in msg.Blocks)
            {
                if (string.Equals(block.BlockType, "text", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(block.Text))
                {
                    parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ConversationHistory, block.Text, "assistant", model));
                }
            }

            List<string> skillsInvoked = [];
            HashSet<string> seenSkills = new(StringComparer.Ordinal);
            List<(CommandCodeToolCall Call, string NormalizedToolName)> toolCallsToEmit = [];

            foreach (CommandCodeContentBlock block in msg.Blocks)
            {
                if (!string.Equals(block.BlockType, "tool_use", StringComparison.Ordinal)
                    || block.ToolCall is null)
                {
                    continue;
                }

                CommandCodeToolCall toolCall = block.ToolCall;
                if (string.Equals(toolCall.Name, "activate_skill", StringComparison.Ordinal)
                    && SafeOptionalText(toolCall.SkillName) is string skill
                    && seenSkills.Add(skill))
                {
                    skillsInvoked.Add(skill);
                }

                toolCallsToEmit.Add((toolCall, MapToolName(toolCall.Name)));
            }

            List<CollectedTokenCount> tokens = [];
            if (usage.InputTokens.HasValue)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.Input, input));
            }

            if (usage.CacheReadTokens.HasValue)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, cacheRead));
            }

            if (usage.CacheWriteTokens.HasValue)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, cacheWrite));
            }

            if (usage.OutputTokens.HasValue)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.Output, output));
            }

            CollectedCost? reportedCost = usage.ReportedCostUsd is > 0 && double.IsFinite(usage.ReportedCostUsd.Value)
                ? new CollectedCost(usage.ReportedCostUsd.Value, "USD")
                : null;

            CollectedRecord parentRecord = new(
                RecordId: parentRecordId,
                HarnessId: CommandCodeHarnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Internal,
                Name: $"{CommandCodeHarnessId}:{model}",
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
                ReportedInputTokens: usage.InputTokens,
                ReportedOutputTokens: usage.OutputTokens);

            records.Add(parentRecord);

            for (int t = 0; t < toolCallsToEmit.Count; t++)
            {
                (CommandCodeToolCall toolCall, string normalizedToolName) = toolCallsToEmit[t];
                records.Add(BuildToolInvokeRecord(
                    parentRecord,
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
                HarnessId: CommandCodeHarnessId,
                NativeSessionId: sessionId,
                Workspace: workspace,
                Title: null,
                ReportedUsage: []));
        }

        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId, importedAtUtc);
        return new CollectedBatch(source, sessions, records, problems);
    }

    /// <summary>
    /// Maps a raw CommandCode tool name to its canonical display name.
    /// </summary>
    /// <param name="rawToolName">Raw tool name from the transcript.</param>
    /// <returns>The canonical tool name.</returns>
    public static string MapToolName(string rawToolName)
    {
        ArgumentNullException.ThrowIfNull(rawToolName);
        return ToolNameMap.TryGetValue(rawToolName, out string? mapped) ? mapped : rawToolName;
    }

    private static CommandCodeMessage ParseMessage(JsonElement msgEl)
    {
        string? role = ReadSafeString(msgEl, "role");
        string? model = ReadSafeString(msgEl, "model");

        List<CommandCodeContentBlock> blocks = [];
        if (msgEl.TryGetProperty("content", out JsonElement contentEl))
        {
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                string? scalarText = contentEl.GetString();
                if (scalarText is not null)
                {
                    blocks.Add(new CommandCodeContentBlock("text", scalarText, null));
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
                        blocks.Add(new CommandCodeContentBlock("text", text, null));
                    }
                    else if (string.Equals(blockType, "tool_use", StringComparison.Ordinal))
                    {
                        string? name = ReadSafeString(blockEl, "name");
                        if (name is null)
                        {
                            continue;
                        }

                        string? rawInputText = null;
                        string? skillName = null;
                        string? subagentType = null;
                        string? commandText = null;

                        if (blockEl.TryGetProperty("input", out JsonElement inputEl)
                            && inputEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        {
                            rawInputText = inputEl.GetRawText();
                            if (inputEl.ValueKind == JsonValueKind.Object)
                            {
                                skillName = ReadStringOnly(inputEl, "name");
                                subagentType = ReadStringOnly(inputEl, "subagent_type");
                                commandText = ReadStringOnly(inputEl, "command");
                            }
                        }

                        blocks.Add(new CommandCodeContentBlock(
                            BlockType: "tool_use",
                            Text: null,
                            ToolCall: new CommandCodeToolCall(name, rawInputText, skillName, subagentType, commandText)));
                    }
                }
            }
        }

        return new CommandCodeMessage(role, model, blocks);
    }

    private static CommandCodeUsage ParseUsage(JsonElement usageEl)
    {
        double? reportedCostUsd = null;
        if (usageEl.TryGetProperty("costUsd", out JsonElement costEl)
            && costEl.ValueKind == JsonValueKind.Number
            && costEl.TryGetDouble(out double costVal)
            && double.IsFinite(costVal))
        {
            reportedCostUsd = costVal;
        }

        return new CommandCodeUsage(
            InputTokens: ReadOptionalNonNegativeLong(usageEl, "inputTokens"),
            OutputTokens: ReadOptionalNonNegativeLong(usageEl, "outputTokens"),
            CacheReadTokens: ReadOptionalNonNegativeLong(usageEl, "cacheReadTokens"),
            CacheWriteTokens: ReadOptionalNonNegativeLong(usageEl, "cacheWriteTokens"),
            ReportedCostUsd: reportedCostUsd);
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parentRecord,
        string sessionId,
        string traceId,
        CommandCodeToolCall toolCall,
        string normalizedToolName,
        int toolIndex,
        HashSet<string> seenRecordIds)
    {
        // CommandCode tool_use blocks carry no native call id; fall back to the block index.
        string safeCallId = string.Create(CultureInfo.InvariantCulture, $"call-{toolIndex}");
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
            HarnessId: CommandCodeHarnessId,
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

    private static string ResolveMessageModel(string? messageModel, string? entryModel, string? resolvedModel)
    {
        string msgModel = SafeOptionalText(messageModel) ?? SafeOptionalText(entryModel) ?? string.Empty;
        string sessionModel = SafeOptionalText(resolvedModel) ?? string.Empty;

        if (msgModel.Length > 0)
        {
            return msgModel;
        }

        if (sessionModel.Length > 0)
        {
            return sessionModel;
        }

        return "unknown";
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
        return $"commandcode-{Convert.ToHexStringLower(digest)[..16]}";
    }

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator) =>
        HarnessJson.EnsureUniqueRecordId(seenRecordIds, candidate, disambiguator);

    private static DateTimeOffset? TryParseTimestamp(string? rawTimestamp) =>
        HarnessJson.TryParseTimestamp(rawTimestamp);

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<CommandCodeParseProblem> parseProblems,
        string sourceKey,
        string sessionId,
        DateTimeOffset importedAtUtc)
    {
        if (parseProblems.Count == 0)
        {
            return [];
        }

        string timestampUtc = importedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        List<CollectedProblem> problems = new(parseProblems.Count);

        foreach (CommandCodeParseProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"commandcode-malformed:{sourceKey}:line-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed CommandCode JSONL line.";

            problems.Add(new CollectedProblem(
                ProblemKey: problemKey,
                Severity: "warning",
                Code: parseProblem.Code,
                Message: message,
                SpanId: null,
                Location: location,
                SessionKey: sessionId,
                HarnessId: CommandCodeHarnessId,
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
