using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Data.Repositories;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace KyberDash.Data.Harnesses.Claude;

/// <summary>
/// Token usage counters extracted from an assistant message in a Claude Code JSONL transcript.
/// </summary>
/// <param name="InputTokens">Exclusive input tokens (<c>input_tokens</c>), when present.</param>
/// <param name="OutputTokens">Output tokens (<c>output_tokens</c>), when present.</param>
/// <param name="CacheReadInputTokens">Cache-read input tokens (<c>cache_read_input_tokens</c>), when present.</param>
/// <param name="CacheCreationInputTokens">Cache-creation input tokens (<c>cache_creation_input_tokens</c>), when present.</param>
/// <param name="ReasoningTokens">Reasoning tokens (<c>reasoning_tokens</c>), when present.</param>
public record ClaudeTranscriptUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadInputTokens,
    long? CacheCreationInputTokens,
    long? ReasoningTokens = null);

/// <summary>
/// Typed representation of one content block inside a Claude Code transcript message.
/// </summary>
/// <param name="BlockType">Content block type (<c>text</c>, <c>thinking</c>, <c>tool_use</c>, or <c>tool_result</c>).</param>
/// <param name="Text">Text payload for <c>text</c> or <c>thinking</c> blocks.</param>
/// <param name="ToolUseId">Tool call correlation identifier (<c>id</c> on <c>tool_use</c>, <c>tool_use_id</c> on <c>tool_result</c>).</param>
/// <param name="ToolName">Tool name on <c>tool_use</c> blocks.</param>
/// <param name="RawInputText">Raw input payload text on <c>tool_use</c> blocks, or <see langword="null"/> when omitted.</param>
/// <param name="InputIsString">Whether <c>input</c> arrived as a JSON string rather than a structured JSON value.</param>
/// <param name="ResultContentText">Extracted result content text on <c>tool_result</c> blocks, or <see langword="null"/> when omitted.</param>
/// <param name="IsError">Whether the <c>tool_result</c> block signaled an error or non-zero exit code.</param>
/// <param name="ExitCode">Optional process exit code reported on a <c>tool_result</c> block.</param>
/// <param name="ErrorCode">Optional machine-readable error code reported on a <c>tool_result</c> block.</param>
public record ClaudeTranscriptContentBlock(
    string BlockType,
    string? Text = null,
    string? ToolUseId = null,
    string? ToolName = null,
    string? RawInputText = null,
    bool InputIsString = false,
    string? ResultContentText = null,
    bool IsError = false,
    long? ExitCode = null,
    string? ErrorCode = null);

/// <summary>
/// Typed representation of the <c>message</c> object on a Claude Code JSONL line.
/// </summary>
/// <param name="Id">Native message identifier (<c>message.id</c>).</param>
/// <param name="Role">Message role (<c>user</c>, <c>assistant</c>, etc.).</param>
/// <param name="Model">Reported model identifier (<c>message.model</c>).</param>
/// <param name="Usage">Reported token usage counters, when present.</param>
/// <param name="ScalarContent">Scalar/object text when <c>message.content</c> is not a block array.</param>
/// <param name="Blocks">Parsed content blocks when <c>message.content</c> is an array.</param>
public record ClaudeTranscriptMessage(
    string? Id,
    string? Role,
    string? Model,
    ClaudeTranscriptUsage? Usage,
    string? ScalarContent,
    IReadOnlyList<ClaudeTranscriptContentBlock> Blocks);

/// <summary>
/// Typed representation of one parsed JSON object line in a Claude Code JSONL transcript.
/// </summary>
/// <param name="RecordType">Top-level entry <c>type</c> (<c>user</c>, <c>assistant</c>, <c>exit</c>, <c>result</c>, etc.).</param>
/// <param name="SessionId">Native session identifier (<c>sessionId</c>).</param>
/// <param name="SessionTitle">Session title extracted from <c>customTitle</c> or <c>aiTitle</c>.</param>
/// <param name="Timestamp">ISO-8601 UTC timestamp string.</param>
/// <param name="TerminationReason">Termination or stop reason (<c>terminationReason</c>, <c>stop_reason</c>, or <c>exit</c>/<c>result</c>).</param>
/// <param name="ExitCode">Reported process exit code (<c>exitCode</c>, <c>exit_code</c>, or <c>code</c>).</param>
/// <param name="AttributionMcpServer">Optional ground-truth MCP server attribution.</param>
/// <param name="Message">Parsed <c>message</c> payload, when present.</param>
/// <param name="RequestId">The provider request id the line carries (<c>requestId</c>), when present.</param>
/// <param name="Subtype">Optional entry subtype (<c>subtype</c>), such as <c>compact_boundary</c>.</param>
/// <param name="IsCompactSummary">Whether the line marks a post-compaction summary (<c>isCompactSummary</c>).</param>
/// <param name="IsSidechain">Whether the line belongs to a sidechain/subagent execution (<c>isSidechain</c>).</param>
/// <param name="AgentId">Optional subagent identifier (<c>agentId</c>).</param>
public record ClaudeTranscriptLine(
    string? RecordType,
    string? SessionId,
    string? SessionTitle,
    string? Timestamp,
    string? TerminationReason,
    int? ExitCode,
    string? AttributionMcpServer,
    ClaudeTranscriptMessage? Message,
    string? RequestId = null,
    string? Subtype = null,
    bool IsCompactSummary = false,
    bool IsSidechain = false,
    string? AgentId = null);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> translating a loaded Claude Code JSONL
/// <see cref="NativeSourceUnit"/> directly into normalized <see cref="CollectedBatch"/> DTOs
/// without touching the filesystem or ambient clock.
/// </summary>
public class ClaudeTranscriptMapper : IHarnessMapper
{
    private const long MergeWindowMs = 60_000;
    private const string FallbackTimestamp = "1970-01-01T00:00:00.000Z";
    private const string UnknownModelFallback = "unknown";
    private const string FallbackToolErrorMessage = "Tool execution failed";
    private const string CompactBoundaryAgentInstanceId = "claude:compact_boundary";
    private const string AgentStemPrefix = "agent-";
    private const string SubagentsDirectoryName = "subagents";
    private const string FallbackSidechainBareId = "sidechain";
    private const string SystemReminderOpenTag = "<system-reminder>";
    private const string SystemReminderCloseTag = "</system-reminder>";

    private readonly ITextTokenizer tokenizer;

    /// <summary>
    /// Initializes a mapper using the default shared <see cref="LocalTextTokenizer"/>.
    /// </summary>
    public ClaudeTranscriptMapper()
        : this(LocalTextTokenizer.Shared)
    {
    }

    /// <summary>
    /// Initializes a mapper that measures tool inputs, withheld payloads, and error details with
    /// <paramref name="tokenizer"/>.
    /// </summary>
    /// <param name="tokenizer">Text tokenizer used for content-part and tool-content token measurement.</param>
    [ActivatorUtilitiesConstructor]
    public ClaudeTranscriptMapper(ITextTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        this.tokenizer = tokenizer;
    }

    /// <inheritdoc />
    public string HarnessId => TranscriptReaderRepository.ClaudeHarnessId;

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _ = importedAtUtc;

        CollectedSource source = new(HarnessId, SourceKind.File, unit.Unit.SourceKey);
        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        SourcePathInfo pathInfo = ParseSourcePathInfo(unit.Unit.SourceKey);
        List<List<ClaudeTranscriptLine>> rawGroups = GroupLinesByUsage(unit.RawText);
        if (rawGroups.Count == 0)
        {
            return new CollectedBatch(source, [], [], []);
        }

        List<MergedLineGroup> mergedGroups = MergeClaudeGroups(rawGroups, pathInfo);
        List<ParsedTurnGroup> keptTurns = BuildKeptTurns(mergedGroups, pathInfo);
        if (keptTurns.Count == 0)
        {
            return new CollectedBatch(source, [], [], []);
        }

        Dictionary<string, ParsedToolResult> sessionToolResults = IndexToolResults(keptTurns);
        int[] ordinals = ComputeWindowIndependentOrdinals(keptTurns);

        List<CollectedSession> sessions = BuildSessions(keptTurns);
        List<CollectedRecord> records = BuildRecords(keptTurns, ordinals, sessionToolResults);

        return new CollectedBatch(source, sessions, records, []);
    }

    private static string ExtractFileStem(string sourceKey)
    {
        string stem = Path.GetFileNameWithoutExtension(sourceKey);
        return SafeOptionalText(stem) ?? string.Empty;
    }

    private static SourcePathInfo ParseSourcePathInfo(string sourceKey)
    {
        string fileStem = ExtractFileStem(sourceKey);
        string[] segments = sourceKey.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        int subagentsIndex = -1;
        for (int i = segments.Length - 2; i >= 0; i--)
        {
            if (string.Equals(segments[i], SubagentsDirectoryName, StringComparison.Ordinal))
            {
                subagentsIndex = i;
                break;
            }
        }

        bool isNestedSubagentFile = subagentsIndex >= 0;
        string? subagentParentSessionId = subagentsIndex > 0
            ? SafeOptionalText(segments[subagentsIndex - 1])
            : null;

        return new SourcePathInfo(fileStem, isNestedSubagentFile, subagentParentSessionId);
    }

    private static List<List<ClaudeTranscriptLine>> GroupLinesByUsage(string rawText)
    {
        List<List<ClaudeTranscriptLine>> rawGroups = [];
        List<ClaudeTranscriptLine> currentGroup = [];

        using StringReader reader = new(rawText);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            ClaudeTranscriptLine? parsed = TryParseLine(rawLine);
            if (parsed is null)
            {
                continue;
            }

            currentGroup.Add(parsed);
            if (parsed.Message?.Usage is not null)
            {
                rawGroups.Add(currentGroup);
                currentGroup = [];
            }
        }

        if (currentGroup.Count > 0)
        {
            rawGroups.Add(currentGroup);
        }

        return rawGroups;
    }

    private static ClaudeTranscriptLine? TryParseLine(string rawLine)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawLine);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement root = doc.RootElement;
            string? recordType = ReadSafeMetadataText(root, "type");
            string? sessionId = ReadSafeMetadataText(root, "sessionId");
            string? sessionTitle = ReadSafeMetadataText(root, "customTitle") ?? ReadSafeMetadataText(root, "aiTitle");
            string? timestamp = ReadStringOnly(root, "timestamp");
            string? server = ReadSafeMetadataText(root, "attributionMcpServer")?.Trim();
            if (string.IsNullOrEmpty(server))
            {
                server = null;
            }

            int? exitCode = null;
            if (TryReadInt(root, "exitCode", out int ec))
            {
                exitCode = ec;
            }

            if (TryReadInt(root, "exit_code", out int ecSnake))
            {
                exitCode = ecSnake;
            }

            string? terminationReason = ReadSafeMetadataText(root, "terminationReason")
                ?? ReadSafeMetadataText(root, "stop_reason");

            if (recordType is "exit" or "result")
            {
                terminationReason = recordType;
                if (TryReadInt(root, "code", out int code))
                {
                    exitCode = code;
                }

                if (TryReadInt(root, "exitCode", out int recEc))
                {
                    exitCode = recEc;
                }
            }

            ClaudeTranscriptMessage? message = null;
            if (root.TryGetProperty("message", out JsonElement msgEl) && msgEl.ValueKind == JsonValueKind.Object)
            {
                message = ParseMessage(msgEl);
            }

            string? subtype = ReadSafeMetadataText(root, "subtype");
            bool isCompactSummary = root.TryGetProperty("isCompactSummary", out JsonElement compactEl)
                && compactEl.ValueKind == JsonValueKind.True;
            bool isSidechain = root.TryGetProperty("isSidechain", out JsonElement sidechainEl)
                && sidechainEl.ValueKind == JsonValueKind.True;
            string? agentId = ReadSafeMetadataText(root, "agentId");

            return new ClaudeTranscriptLine(
                RecordType: recordType,
                SessionId: sessionId,
                SessionTitle: sessionTitle,
                Timestamp: timestamp,
                TerminationReason: terminationReason,
                ExitCode: exitCode,
                AttributionMcpServer: server,
                Message: message,
                RequestId: ReadSafeMetadataText(root, "requestId"),
                Subtype: subtype,
                IsCompactSummary: isCompactSummary,
                IsSidechain: isSidechain,
                AgentId: agentId);
        }
    }

    private static ClaudeTranscriptMessage ParseMessage(JsonElement msgEl)
    {
        string? id = ReadSafeMetadataText(msgEl, "id");
        string? role = ReadSafeMetadataText(msgEl, "role");
        string? model = ReadSafeMetadataText(msgEl, "model");
        ClaudeTranscriptUsage? usage = TryParseUsage(msgEl);

        string? scalarContent = null;
        List<ClaudeTranscriptContentBlock> blocks = [];

        if (msgEl.TryGetProperty("content", out JsonElement contentEl))
        {
            if (contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement blockEl in contentEl.EnumerateArray())
                {
                    if (blockEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    ClaudeTranscriptContentBlock? block = ParseContentBlock(blockEl);
                    if (block is not null)
                    {
                        blocks.Add(block);
                    }
                }
            }
            else if (contentEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                scalarContent = ExtractRawContentText(contentEl);
            }
        }

        return new ClaudeTranscriptMessage(
            Id: id,
            Role: role,
            Model: model,
            Usage: usage,
            ScalarContent: scalarContent,
            Blocks: blocks);
    }

    private static ClaudeTranscriptUsage? TryParseUsage(JsonElement msgEl)
    {
        if (!msgEl.TryGetProperty("usage", out JsonElement usageEl) || usageEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ClaudeTranscriptUsage(
            InputTokens: ReadOptionalNonNegLong(usageEl, "input_tokens"),
            OutputTokens: ReadOptionalNonNegLong(usageEl, "output_tokens"),
            CacheReadInputTokens: ReadOptionalNonNegLong(usageEl, "cache_read_input_tokens"),
            CacheCreationInputTokens: ReadOptionalNonNegLong(usageEl, "cache_creation_input_tokens"),
            ReasoningTokens: ReadOptionalNonNegLong(usageEl, "reasoning_tokens"));
    }

    private static ClaudeTranscriptContentBlock? ParseContentBlock(JsonElement blockEl)
    {
        string? blockType = ReadSafeMetadataText(blockEl, "type");
        if (string.IsNullOrEmpty(blockType))
        {
            return null;
        }

        if (string.Equals(blockType, "text", StringComparison.Ordinal))
        {
            string? text = ExtractPropertyAsContentText(blockEl, "text");
            return string.IsNullOrEmpty(text)
                ? null
                : new ClaudeTranscriptContentBlock("text", Text: text);
        }

        if (string.Equals(blockType, "thinking", StringComparison.Ordinal))
        {
            string? text = ExtractPropertyAsContentText(blockEl, "thinking")
                ?? ExtractPropertyAsContentText(blockEl, "text");
            return string.IsNullOrEmpty(text)
                ? null
                : new ClaudeTranscriptContentBlock("thinking", Text: text);
        }

        if (string.Equals(blockType, "tool_use", StringComparison.Ordinal))
        {
            string id = ReadSafeMetadataText(blockEl, "id") ?? string.Empty;
            string name = ReadSafeMetadataText(blockEl, "name") ?? string.Empty;
            if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name))
            {
                return null;
            }

            string? rawInputText = null;
            bool inputIsString = false;
            if (blockEl.TryGetProperty("input", out JsonElement inputEl)
                && inputEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                inputIsString = inputEl.ValueKind == JsonValueKind.String;
                rawInputText = inputIsString
                    ? (inputEl.GetString() ?? string.Empty)
                    : inputEl.GetRawText();
            }

            return new ClaudeTranscriptContentBlock(
                BlockType: "tool_use",
                ToolUseId: id,
                ToolName: name,
                RawInputText: rawInputText,
                InputIsString: inputIsString);
        }

        if (string.Equals(blockType, "tool_result", StringComparison.Ordinal))
        {
            string toolCallId = ReadSafeMetadataText(blockEl, "tool_use_id")
                ?? ReadSafeMetadataText(blockEl, "id")
                ?? string.Empty;

            bool isError = (blockEl.TryGetProperty("is_error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.True)
                || (TryReadLong(blockEl, "exit_code", out long ec1) && ec1 != 0)
                || (TryReadLong(blockEl, "exitCode", out long ec2) && ec2 != 0);

            long? exitCode = TryReadLong(blockEl, "exit_code", out long exit1)
                ? exit1
                : (TryReadLong(blockEl, "exitCode", out long exit2) ? exit2 : null);

            string? errorCode = ReadSafeMetadataText(blockEl, "error_code")
                ?? ReadSafeMetadataText(blockEl, "errorCode");

            string? resultContentText = ExtractToolResultText(blockEl);
            return new ClaudeTranscriptContentBlock(
                BlockType: "tool_result",
                ToolUseId: toolCallId,
                ResultContentText: resultContentText,
                IsError: isError,
                ExitCode: exitCode,
                ErrorCode: errorCode);
        }

        return null;
    }

    private static List<MergedLineGroup> MergeClaudeGroups(
        List<List<ClaudeTranscriptLine>> rawGroups,
        SourcePathInfo pathInfo)
    {
        List<MergedLineGroup> merged = [];
        foreach (List<ClaudeTranscriptLine> group in rawGroups)
        {
            ClaudeTranscriptLine lastLine = group[^1];
            UsageSnapshot? usageInfo = ExtractUsageSnapshot(lastLine, pathInfo);

            if (merged.Count > 0)
            {
                MergedLineGroup prev = merged[^1];
                UsageSnapshot? prevForMatch = prev.Usage is null
                    ? null
                    : prev.Usage with { MessageId = prev.MessageId ?? prev.Usage.MessageId };
                if (IsMatchingClaudeUsage(prevForMatch, usageInfo))
                {
                    prev.Lines.AddRange(group);
                    string? mergedMessageId = prev.MessageId ?? usageInfo!.MessageId;
                    UsageSnapshot mergedUsage = prev.Usage! with
                    {
                        MessageId = mergedMessageId,
                        Model = !string.IsNullOrWhiteSpace(usageInfo!.Model) ? usageInfo.Model : prev.Usage.Model,
                        Usage = usageInfo.Usage,
                        Timestamp = prev.Usage.Timestamp ?? usageInfo.Timestamp,
                    };
                    merged[^1] = prev with { MessageId = mergedMessageId, Usage = mergedUsage };

                    continue;
                }
            }

            merged.Add(new MergedLineGroup([.. group], usageInfo?.MessageId, usageInfo));
        }

        return merged;
    }

    private static UsageSnapshot? ExtractUsageSnapshot(ClaudeTranscriptLine line, SourcePathInfo pathInfo)
    {
        if (line.Message?.Usage is null)
        {
            return null;
        }

        string resolvedSessionId = ResolveSessionId(line.SessionId, line.IsSidechain, line.AgentId, pathInfo)
            ?? pathInfo.FileStem;

        return new UsageSnapshot(
            MessageId: line.Message.Id,
            SessionId: resolvedSessionId,
            Model: line.Message.Model,
            Usage: line.Message.Usage,
            Timestamp: line.Timestamp);
    }

    private static bool IsMatchingClaudeUsage(UsageSnapshot? prev, UsageSnapshot? next)
    {
        if (prev is null || next is null)
        {
            return false;
        }

        if (!string.Equals(prev.SessionId, next.SessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(prev.MessageId) && !string.IsNullOrEmpty(next.MessageId))
        {
            return string.Equals(prev.MessageId, next.MessageId, StringComparison.Ordinal);
        }

        if (!string.Equals(prev.Model, next.Model, StringComparison.Ordinal))
        {
            return false;
        }

        if ((prev.Usage.InputTokens ?? 0L) != (next.Usage.InputTokens ?? 0L)
            || (prev.Usage.OutputTokens ?? 0L) != (next.Usage.OutputTokens ?? 0L)
            || (prev.Usage.CacheReadInputTokens ?? 0L) != (next.Usage.CacheReadInputTokens ?? 0L)
            || (prev.Usage.CacheCreationInputTokens ?? 0L) != (next.Usage.CacheCreationInputTokens ?? 0L)
            || (prev.Usage.ReasoningTokens ?? 0L) != (next.Usage.ReasoningTokens ?? 0L))
        {
            return false;
        }

        if (!HasNonZeroUsage(prev.Usage))
        {
            return false;
        }

        string prevTs = prev.Timestamp ?? FallbackTimestamp;
        string nextTs = next.Timestamp ?? FallbackTimestamp;
        if (DateTimeOffset.TryParse(prevTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset prevDt)
            && DateTimeOffset.TryParse(nextTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset nextDt))
        {
            if (Math.Abs((nextDt - prevDt).TotalMilliseconds) > MergeWindowMs)
            {
                return false;
            }
        }

        return true;
    }

    private static List<ParsedTurnGroup> BuildKeptTurns(List<MergedLineGroup> mergedGroups, SourcePathInfo pathInfo)
    {
        List<ParsedTurnGroup> turns = [];
        HashSet<string> pendingCompactionSessions = new(StringComparer.Ordinal);

        foreach (MergedLineGroup mergedGroup in mergedGroups)
        {
            bool groupHasCompaction = mergedGroup.Lines.Any(IsCompactionMarker);
            ParsedTurnGroup? parsed = ParseTurnGroup(mergedGroup.Lines, mergedGroup.Usage, pathInfo);
            string? sessionBucketKey = parsed?.SessionId
                ?? mergedGroup.Usage?.SessionId
                ?? ResolveGroupFallbackSessionKey(mergedGroup.Lines, pathInfo);

            if (parsed is null)
            {
                if (groupHasCompaction && sessionBucketKey is not null)
                {
                    _ = pendingCompactionSessions.Add(sessionBucketKey);
                }

                continue;
            }

            if (!HasNonZeroUsage(parsed.Usage))
            {
                if (groupHasCompaction && sessionBucketKey is not null)
                {
                    _ = pendingCompactionSessions.Add(sessionBucketKey);
                }

                if (turns.Count > 0 && HasTurnContent(parsed))
                {
                    int targetIndex = FindLastTurnIndexForSession(turns, parsed.SessionId);
                    if (targetIndex >= 0)
                    {
                        turns[targetIndex] = MergeTailTurn(turns[targetIndex], parsed);
                    }
                }

                continue;
            }

            bool hasPendingCompaction = sessionBucketKey is not null && pendingCompactionSessions.Remove(sessionBucketKey);
            if (groupHasCompaction || hasPendingCompaction)
            {
                parsed = parsed with { AgentInstanceId = CompactBoundaryAgentInstanceId };
            }

            turns.Add(parsed);
        }

        return turns;
    }

    private static int FindLastTurnIndexForSession(IReadOnlyList<ParsedTurnGroup> turns, string? sessionId)
    {
        for (int i = turns.Count - 1; i >= 0; i--)
        {
            if (string.Equals(turns[i].SessionId, sessionId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsCompactionMarker(ClaudeTranscriptLine line) =>
        (string.Equals(line.RecordType, "system", StringComparison.Ordinal)
            && string.Equals(line.Subtype, "compact_boundary", StringComparison.Ordinal))
        || line.IsCompactSummary;

    private static string? ResolveGroupFallbackSessionKey(
        List<ClaudeTranscriptLine> lines,
        SourcePathInfo pathInfo)
    {
        string? sessionId = null;
        bool isSidechain = false;
        string? agentId = null;
        foreach (ClaudeTranscriptLine line in lines)
        {
            sessionId ??= line.SessionId;
            if (line.IsSidechain)
            {
                isSidechain = true;
            }

            agentId ??= line.AgentId;
        }

        return ResolveSessionId(sessionId, isSidechain, agentId, pathInfo);
    }

    private static string? ResolveSessionId(
        string? lineSessionId,
        bool isSidechain,
        string? agentId,
        SourcePathInfo pathInfo)
    {
        bool isolateAsSubagent = isSidechain
            || !string.IsNullOrEmpty(agentId)
            || pathInfo.IsNestedSubagentFile;

        if (!isolateAsSubagent)
        {
            return !string.IsNullOrEmpty(lineSessionId)
                ? lineSessionId
                : (string.IsNullOrEmpty(pathInfo.FileStem) ? null : pathInfo.FileStem);
        }

        string? parentSessionId = !string.IsNullOrEmpty(lineSessionId)
            ? lineSessionId
            : (pathInfo.SubagentParentSessionId ?? (string.IsNullOrEmpty(pathInfo.FileStem) ? null : pathInfo.FileStem));
        if (string.IsNullOrEmpty(parentSessionId))
        {
            return null;
        }

        string bareId = ResolveSubagentBareId(agentId, pathInfo);
        return $"{parentSessionId}#agent-{bareId}";
    }

    private static string ResolveSubagentBareId(string? agentId, SourcePathInfo pathInfo)
    {
        if (!string.IsNullOrEmpty(agentId))
        {
            string trimmed = agentId.Trim();
            return trimmed.StartsWith(AgentStemPrefix, StringComparison.Ordinal) && trimmed.Length > AgentStemPrefix.Length
                ? trimmed[AgentStemPrefix.Length..]
                : trimmed;
        }

        if (pathInfo.FileStem.StartsWith(AgentStemPrefix, StringComparison.Ordinal)
            && pathInfo.FileStem.Length > AgentStemPrefix.Length)
        {
            return pathInfo.FileStem[AgentStemPrefix.Length..];
        }

        if (pathInfo.IsNestedSubagentFile && !string.IsNullOrEmpty(pathInfo.FileStem))
        {
            return pathInfo.FileStem;
        }

        return FallbackSidechainBareId;
    }

    private static ParsedTurnGroup? ParseTurnGroup(
        List<ClaudeTranscriptLine> lines,
        UsageSnapshot? usageSnapshot,
        SourcePathInfo pathInfo)
    {
        string? discoveredSessionId = null;
        bool isSidechain = false;
        string? discoveredAgentId = null;
        string? nativeRecordId = usageSnapshot?.MessageId;
        string? requestId = null;
        string? sessionTitle = null;
        string? terminationReason = null;
        int? exitCode = null;
        string? correctionRule = null;
        List<ParsedPart> parts = [];
        List<ParsedToolCall> toolCalls = [];
        List<ParsedToolResult> toolResults = [];

        foreach (ClaudeTranscriptLine line in lines)
        {
            discoveredSessionId ??= line.SessionId;
            if (line.IsSidechain)
            {
                isSidechain = true;
            }

            discoveredAgentId ??= line.AgentId;
            sessionTitle ??= line.SessionTitle;
            if (string.Equals(line.Message?.Role, "assistant", StringComparison.Ordinal))
            {
                requestId ??= line.RequestId;
            }

            if (line.ExitCode.HasValue)
            {
                exitCode = line.ExitCode.Value;
            }

            if (line.TerminationReason is not null)
            {
                terminationReason = line.TerminationReason;
            }

            if (line.Message is null)
            {
                continue;
            }

            if (nativeRecordId is null && line.Message.Usage is not null)
            {
                nativeRecordId = line.Message.Id;
            }

            string? role = line.Message.Role;
            string? server = line.AttributionMcpServer;

            if (line.Message.ScalarContent is not null)
            {
                string text = line.Message.ScalarContent;
                if (!string.IsNullOrEmpty(text))
                {
                    if (string.Equals(role, "user", StringComparison.Ordinal))
                    {
                        CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(text);
                        if (check.Matched)
                        {
                            correctionRule = check.Rule;
                        }
                    }

                    parts.Add(new ParsedPart(ContentBucket.ConversationHistory, text, server, role));
                }

                continue;
            }

            foreach (ClaudeTranscriptContentBlock block in line.Message.Blocks)
            {
                if (string.Equals(block.BlockType, "text", StringComparison.Ordinal))
                {
                    string? text = block.Text;
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    if (string.Equals(role, "user", StringComparison.Ordinal))
                    {
                        CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(text);
                        if (check.Matched)
                        {
                            correctionRule = check.Rule;
                        }
                    }

                    parts.Add(new ParsedPart(ContentBucket.ConversationHistory, text, server, role));
                }
                else if (string.Equals(block.BlockType, "thinking", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(block.Text))
                    {
                        parts.Add(new ParsedPart(ContentBucket.ConversationHistory, block.Text, server, role));
                    }
                }
                else if (string.Equals(block.BlockType, "tool_result", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(block.ToolUseId))
                    {
                        toolResults.Add(new ParsedToolResult(
                            ToolCallId: block.ToolUseId,
                            Content: block.ResultContentText,
                            IsError: block.IsError,
                            Timestamp: line.Timestamp,
                            ExitCode: block.ExitCode,
                            ErrorCode: block.ErrorCode));
                    }

                    if (!string.IsNullOrEmpty(block.ResultContentText))
                    {
                        parts.Add(new ParsedPart(ContentBucket.ToolResultContent, block.ResultContentText, server, role));
                    }
                }
                else if (string.Equals(block.BlockType, "tool_use", StringComparison.Ordinal))
                {
                    string id = block.ToolUseId ?? string.Empty;
                    string name = block.ToolName ?? string.Empty;
                    if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    string resolvedName = string.IsNullOrWhiteSpace(name) ? UnknownModelFallback : name;
                    string toolPartText = FormatToolUsePartText(resolvedName, block.RawInputText);
                    parts.Add(new ParsedPart(ContentBucket.ConversationHistory, toolPartText, server, role));

                    toolCalls.Add(new ParsedToolCall(
                        Id: id,
                        Name: resolvedName,
                        RawInputText: block.RawInputText,
                        InputIsString: block.InputIsString,
                        Timestamp: line.Timestamp));
                }
            }
        }

        if (parts.Count == 0
            && discoveredSessionId is null
            && toolCalls.Count == 0
            && toolResults.Count == 0
            && usageSnapshot is null)
        {
            return null;
        }

        string? sessionId = usageSnapshot is not null && !string.IsNullOrEmpty(usageSnapshot.SessionId)
            ? usageSnapshot.SessionId
            : ResolveSessionId(discoveredSessionId, isSidechain, discoveredAgentId, pathInfo);

        return new ParsedTurnGroup(
            SessionId: sessionId,
            NativeRecordId: nativeRecordId,
            RequestId: requestId,
            Timestamp: usageSnapshot?.Timestamp ?? FallbackTimestamp,
            Model: usageSnapshot?.Model,
            Usage: usageSnapshot?.Usage,
            SessionTitle: FormatSessionTitle(sessionTitle),
            TerminationReason: terminationReason,
            ExitCode: exitCode,
            CorrectionRule: correctionRule,
            AgentInstanceId: null,
            Parts: parts,
            ToolCalls: toolCalls,
            ToolResults: toolResults);
    }

    private static string FormatToolUsePartText(string toolName, string? rawInputText) =>
        string.IsNullOrEmpty(rawInputText)
            ? toolName
            : $"{toolName} {rawInputText}";

    private static bool HasNonZeroUsage(ClaudeTranscriptUsage? usage) =>
        usage is not null
        && ((usage.InputTokens ?? 0L) != 0
            || (usage.OutputTokens ?? 0L) != 0
            || (usage.CacheReadInputTokens ?? 0L) != 0
            || (usage.CacheCreationInputTokens ?? 0L) != 0
            || (usage.ReasoningTokens ?? 0L) != 0);

    private static bool HasTurnContent(ParsedTurnGroup turn) =>
        turn.Parts.Count > 0 || turn.ToolCalls.Count > 0 || turn.ToolResults.Count > 0;

    private static ParsedTurnGroup MergeTailTurn(ParsedTurnGroup head, ParsedTurnGroup tail) =>
        head with
        {
            Parts = [.. head.Parts, .. tail.Parts],
            ToolCalls = [.. head.ToolCalls, .. tail.ToolCalls],
            ToolResults = [.. head.ToolResults, .. tail.ToolResults],
            SessionTitle = head.SessionTitle ?? tail.SessionTitle,
        };

    private static Dictionary<string, ParsedToolResult> IndexToolResults(IReadOnlyList<ParsedTurnGroup> turns)
    {
        Dictionary<string, ParsedToolResult> map = new(StringComparer.Ordinal);
        foreach (ParsedTurnGroup turn in turns)
        {
            foreach (ParsedToolResult result in turn.ToolResults)
            {
                if (!string.IsNullOrEmpty(result.ToolCallId))
                {
                    map[result.ToolCallId] = result;
                }
            }
        }

        return map;
    }

    private static int[] ComputeWindowIndependentOrdinals(IReadOnlyList<ParsedTurnGroup> turns)
    {
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        int[] ordinals = new int[turns.Count];

        for (int i = 0; i < turns.Count; i++)
        {
            ParsedTurnGroup turn = turns[i];
            string key = $"{TranscriptReaderRepository.ClaudeHarnessId}\0{turn.SessionId}\0{turn.Timestamp}";
            int current = seen.GetValueOrDefault(key, 0);
            ordinals[i] = current;
            seen[key] = current + 1;
        }

        return ordinals;
    }

    private List<CollectedSession> BuildSessions(IReadOnlyList<ParsedTurnGroup> turns)
    {
        List<string> sessionOrder = [];
        Dictionary<string, string?> titlesBySession = new(StringComparer.Ordinal);

        foreach (ParsedTurnGroup turn in turns)
        {
            if (string.IsNullOrWhiteSpace(turn.SessionId))
            {
                continue;
            }

            if (!titlesBySession.TryGetValue(turn.SessionId, out string? existingTitle))
            {
                sessionOrder.Add(turn.SessionId);
                titlesBySession[turn.SessionId] = turn.SessionTitle;
            }
            else if (existingTitle is null && turn.SessionTitle is not null)
            {
                titlesBySession[turn.SessionId] = turn.SessionTitle;
            }
        }

        List<CollectedSession> sessions = new(sessionOrder.Count);
        foreach (string sessionId in sessionOrder)
        {
            sessions.Add(new CollectedSession(
                SessionKey: sessionId,
                HarnessId: HarnessId,
                NativeSessionId: sessionId,
                Workspace: null,
                Title: titlesBySession[sessionId],
                ReportedUsage: []));
        }

        return sessions;
    }

    private List<CollectedRecord> BuildRecords(
        IReadOnlyList<ParsedTurnGroup> turns,
        int[] ordinals,
        IReadOnlyDictionary<string, ParsedToolResult> sessionToolResults)
    {
        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        for (int i = 0; i < turns.Count; i++)
        {
            ParsedTurnGroup turn = turns[i];
            CollectedRecord parent = BuildParentRecord(turn, ordinals[i]);
            if (!seenRecordIds.Add(parent.RecordId))
            {
                continue;
            }

            records.Add(parent);

            for (int toolIndex = 0; toolIndex < turn.ToolCalls.Count; toolIndex++)
            {
                ParsedToolCall toolCall = turn.ToolCalls[toolIndex];
                _ = sessionToolResults.TryGetValue(toolCall.Id, out ParsedToolResult? matchedResult);
                if (matchedResult is null)
                {
                    foreach (ParsedToolResult candidate in turn.ToolResults)
                    {
                        if (string.Equals(candidate.ToolCallId, toolCall.Id, StringComparison.Ordinal))
                        {
                            matchedResult = candidate;
                            break;
                        }
                    }
                }

                CollectedRecord child = BuildToolRecord(parent, toolCall, matchedResult, toolIndex);
                if (seenRecordIds.Add(child.RecordId))
                {
                    records.Add(child);
                }
            }
        }

        return records;
    }

    private CollectedRecord BuildParentRecord(ParsedTurnGroup turn, int ordinal)
    {
        string nativeRecordId = ResolveNativeRecordId(turn, ordinal);
        string spanId = $"{CanonSynth.SynthSpanPrefix}{HarnessId}:{turn.SessionId}:{nativeRecordId}";
        string traceId = !string.IsNullOrEmpty(turn.SessionId)
            ? $"{CanonSynth.SynthSpanPrefix}{HarnessId}:{turn.SessionId}"
            : $"{CanonSynth.SynthSpanPrefix}{HarnessId}:~{nativeRecordId}";

        DateTimeOffset startedAt = ParseUtcTimestamp(turn.Timestamp) ?? DateTimeOffset.UnixEpoch;
        RecordStatus status = turn.ExitCode is not null and not 0
            ? RecordStatus.Error
            : string.Equals(turn.TerminationReason, "task_complete", StringComparison.Ordinal)
                ? RecordStatus.Ok
                : RecordStatus.Unspecified;

        (IReadOnlyList<CollectedTokenCount> tokens, long? reportedInput, long? reportedOutput) = BuildTokenCounts(turn.Usage);
        List<CollectedContentPart> parts = BuildContentParts(turn.Parts, turn.Model);
        string displayModel = turn.Model ?? UnknownModelFallback;

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: HarnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: $"{TranscriptReaderRepository.ClaudeProviderId}:{displayModel}",
            StartedAt: startedAt,
            Status: status,
            SessionKey: turn.SessionId,
            ParentRecordId: null,
            TraceId: traceId,
            Model: turn.Model,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: tokens,
            ReportedCost: null,
            Parts: parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: turn.AgentInstanceId,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(
                ErrorMessage: null,
                EditFailures: null,
                CorrectionRule: turn.CorrectionRule,
                DeclaredContextWindow: null),
            ReportedInputTokens: reportedInput,
            ReportedOutputTokens: reportedOutput,
            ResponseId: OtlpJoinIds.WithinBound(string.IsNullOrEmpty(turn.NativeRecordId) ? null : turn.NativeRecordId),
            RequestId: OtlpJoinIds.WithinBound(turn.RequestId));
    }

    private CollectedRecord BuildToolRecord(
        CollectedRecord parent,
        ParsedToolCall toolCall,
        ParsedToolResult? toolResult,
        int index)
    {
        string toolKeyId = !string.IsNullOrEmpty(toolCall.Id)
            ? toolCall.Id
            : index.ToString(CultureInfo.InvariantCulture);
        string rawSerializedArgs = toolCall.RawInputText ?? "{}";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{toolKeyId}:{toolCall.Name}:{rawSerializedArgs}"));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];
        string spanId = $"{parent.RecordId}-t{toolHash}";

        DateTimeOffset startedAt = ParseUtcTimestamp(toolCall.Timestamp) ?? parent.StartedAt;
        DateTimeOffset? endedAt = null;
        long? reportedDurationMs = null;

        DateTimeOffset? parsedCallTs = ParseUtcTimestamp(toolCall.Timestamp);
        DateTimeOffset? parsedResultTs = ParseUtcTimestamp(toolResult?.Timestamp);
        if (parsedCallTs.HasValue && parsedResultTs.HasValue)
        {
            long diffMs = (long)Math.Round((parsedResultTs.Value - parsedCallTs.Value).TotalMilliseconds);
            if (diffMs >= 0)
            {
                endedAt = parsedResultTs.Value;
                reportedDurationMs = diffMs;
            }
        }

        RecordStatus status = toolResult is null
            ? RecordStatus.Unset
            : (toolResult.IsError ? RecordStatus.Error : RecordStatus.Ok);

        CollectedToolContent? inputContent = toolCall.RawInputText is not null
            ? ToolContentMeasurer.Measure(toolCall.RawInputText, tokenizer, parent.Model, CanonSynth.MaxToolArgumentsBytes)
                .ToToolContent(includeFingerprint: true)
            : null;

        CollectedToolContent? resultContent = null;
        List<CollectedContentPart> parts = [];
        if (toolResult?.Content is not null)
        {
            int partPosition = 0;
            ToolContentMeasurement resultMeasurement = AppendMeasuredContentParts(
                parts,
                ref partPosition,
                ContentBucket.ToolResultContent,
                toolResult.Content,
                server: null,
                role: null,
                parent.Model);
            resultContent = resultMeasurement.ToToolContent(includeFingerprint: false);
        }

        (string? mcpServerName, string? mcpToolName) = ParseMcpToolNames(toolCall.Name);
        bool? succeeded = toolResult is null ? null : !toolResult.IsError;
        CollectedError? error = toolResult is { IsError: true }
            ? new CollectedError(
                Message: BuildSafeToolErrorMessage(toolResult.Content, parent.Model),
                Code: toolResult.ErrorCode)
            : null;

        CollectedToolCall tool = new(
            CallId: toolCall.Id,
            ToolName: toolCall.Name,
            McpServerName: mcpServerName,
            McpToolName: mcpToolName,
            Input: inputContent,
            Result: resultContent,
            Succeeded: succeeded,
            Error: error,
            ExitCode: toolResult?.ExitCode);

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: HarnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: toolCall.Name,
            StartedAt: startedAt,
            Status: status,
            SessionKey: parent.SessionKey,
            ParentRecordId: parent.RecordId,
            TraceId: parent.TraceId,
            Model: null,
            EndedAt: endedAt,
            ReportedDurationMs: reportedDurationMs,
            Tokens: [],
            ReportedCost: null,
            Parts: parts,
            Tool: tool,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null));
    }

    private string ResolveNativeRecordId(ParsedTurnGroup turn, int ordinal)
    {
        if (!string.IsNullOrEmpty(turn.NativeRecordId))
        {
            return turn.NativeRecordId;
        }

        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{turn.SessionId}\0{turn.Timestamp}\0llm.invoke\0{ordinal}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static (IReadOnlyList<CollectedTokenCount> Tokens, long? ReportedInput, long? ReportedOutput) BuildTokenCounts(
        ClaudeTranscriptUsage? usage)
    {
        if (usage is null)
        {
            return ([], null, null);
        }

        List<CollectedTokenCount> tokens = [];
        long inputSum = 0L;
        bool hasInput = false;

        if (usage.InputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, usage.InputTokens.Value));
            inputSum += usage.InputTokens.Value;
            hasInput = true;
        }

        if (usage.CacheReadInputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, usage.CacheReadInputTokens.Value));
            inputSum += usage.CacheReadInputTokens.Value;
            hasInput = true;
        }

        if (usage.CacheCreationInputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, usage.CacheCreationInputTokens.Value));
            inputSum += usage.CacheCreationInputTokens.Value;
            hasInput = true;
        }

        if (usage.OutputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, usage.OutputTokens.Value));
        }

        if (usage.ReasoningTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Reasoning, usage.ReasoningTokens.Value));
        }

        long? reportedInput = hasInput ? inputSum : null;
        long? reportedOutput = usage.OutputTokens;
        return (tokens, reportedInput, reportedOutput);
    }

    private List<CollectedContentPart> BuildContentParts(IReadOnlyList<ParsedPart> parts, string? model)
    {
        List<CollectedContentPart> collected = new(parts.Count);
        int position = 0;
        foreach (ParsedPart part in parts)
        {
            _ = AppendMeasuredContentParts(
                collected,
                ref position,
                part.Bucket,
                part.Text,
                part.Server,
                part.Role,
                model);
        }

        return collected;
    }

    private ToolContentMeasurement AppendMeasuredContentParts(
        List<CollectedContentPart> collected,
        ref int position,
        ContentBucket bucket,
        string text,
        string? server,
        string? role,
        string? model)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(text, tokenizer, model, CanonSynth.MaxToolResultBytes);
        if (measurement.IsDocument)
        {
            collected.Add(new CollectedContentPart(
                Position: position++,
                Bucket: bucket,
                PlainText: null,
                Withheld: measurement.ToWithheldText(),
                ReportedTokens: null,
                Server: server,
                Role: role,
                Resident: null,
                OriginalUtf8BytesWhenTruncated: null));
            return measurement;
        }

        if (measurement.Utf8Bytes <= CanonSynth.MaxToolResultBytes)
        {
            collected.Add(new CollectedContentPart(
                Position: position++,
                Bucket: bucket,
                PlainText: measurement.PlainText,
                Withheld: null,
                ReportedTokens: null,
                Server: server,
                Role: role,
                Resident: null,
                OriginalUtf8BytesWhenTruncated: null));
            return measurement;
        }

        int safeCut = ComputeSystemReminderSafeCut(text, measurement.PlainText ?? string.Empty);
        if (safeCut > 0)
        {
            string retainedPrefix = text[..safeCut];
            collected.Add(new CollectedContentPart(
                Position: position++,
                Bucket: bucket,
                PlainText: retainedPrefix,
                Withheld: null,
                ReportedTokens: null,
                Server: server,
                Role: role,
                Resident: null,
                OriginalUtf8BytesWhenTruncated: measurement.Utf8Bytes));
        }

        string unretainedTail = text[safeCut..];
        if (unretainedTail.Length > 0)
        {
            ToolContentMeasurement tailMeasurement = ToolContentMeasurer.Measure(
                unretainedTail,
                tokenizer,
                model,
                CanonSynth.MaxToolResultBytes);
            collected.Add(new CollectedContentPart(
                Position: position++,
                Bucket: bucket,
                PlainText: null,
                Withheld: tailMeasurement.ToWithheldText(),
                ReportedTokens: null,
                Server: server,
                Role: role,
                Resident: null,
                OriginalUtf8BytesWhenTruncated: null));
        }

        return measurement;
    }

    private static int ComputeSystemReminderSafeCut(string fullText, string truncatedPrefix)
    {
        int cut = truncatedPrefix.Length;
        if (cut <= 0 || cut >= fullText.Length)
        {
            return cut;
        }

        // Avoid tearing an opening <system-reminder> tag across the 64 KiB byte boundary.
        int lastLt = truncatedPrefix.LastIndexOf('<');
        if (lastLt >= 0)
        {
            ReadOnlySpan<char> trailingSlice = truncatedPrefix.AsSpan(lastLt);
            if (SystemReminderOpenTag.AsSpan().StartsWith(trailingSlice, StringComparison.Ordinal)
                && fullText.AsSpan(lastLt).StartsWith(SystemReminderOpenTag, StringComparison.Ordinal))
            {
                cut = lastLt;
            }
        }

        if (cut <= 0)
        {
            return 0;
        }

        // Back up before any unclosed <system-reminder> so StripInstructionBlocks never sees a broken block.
        int lastOpen = fullText.LastIndexOf(SystemReminderOpenTag, cut - 1, cut, StringComparison.Ordinal);
        if (lastOpen >= 0)
        {
            int searchStart = lastOpen + SystemReminderOpenTag.Length;
            int closeCount = cut - searchStart;
            int matchingClose = closeCount >= SystemReminderCloseTag.Length
                ? fullText.IndexOf(SystemReminderCloseTag, searchStart, closeCount, StringComparison.Ordinal)
                : -1;
            if (matchingClose < 0)
            {
                cut = lastOpen;
            }
        }

        return cut;
    }

    private static (string? ServerName, string? ToolName) ParseMcpToolNames(string toolName)
    {
        const string McpPrefix = "mcp__";
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

    private string BuildSafeToolErrorMessage(string? rawContent, string? model)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return FallbackToolErrorMessage;
        }

        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(rawContent, tokenizer, model, CanonSynth.MaxToolResultBytes);
        if (!measurement.IsDocument && !string.IsNullOrWhiteSpace(measurement.PlainText))
        {
            return measurement.PlainText;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                string? extracted = ReadSafeMetadataText(doc.RootElement, "error")
                    ?? ReadSafeMetadataText(doc.RootElement, "message");
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    return extracted;
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to fixed message when nested JSON cannot be unwrapped directly.
        }

        return FallbackToolErrorMessage;
    }

    private static string? ExtractToolResultText(JsonElement block)
    {
        if (block.TryGetProperty("content", out JsonElement rawContent)
            && rawContent.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (rawContent.ValueKind == JsonValueKind.Array)
            {
                List<string> items = [];
                foreach (JsonElement item in rawContent.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        items.Add(ExtractPropertyAsContentText(item, "text") ?? item.GetRawText());
                    }
                    else
                    {
                        items.Add(ExtractRawContentText(item));
                    }
                }

                return string.Join('\n', items);
            }

            if (rawContent.ValueKind == JsonValueKind.Object)
            {
                return ExtractPropertyAsContentText(rawContent, "text") ?? rawContent.GetRawText();
            }

            return ExtractRawContentText(rawContent);
        }

        if (block.TryGetProperty("text", out JsonElement fallbackText)
            && fallbackText.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return ExtractRawContentText(fallbackText);
        }

        return null;
    }

    private static string? ExtractPropertyAsContentText(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el)
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        string text = ExtractRawContentText(el);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string ExtractRawContentText(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            _ => el.GetRawText(),
        };

    private static string? ReadStringOnly(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = el.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ReadSafeMetadataText(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el))
        {
            return null;
        }

        string? value = el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
            _ => null,
        };

        return SafeOptionalText(value);
    }

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static string? FormatSessionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || JsonDocumentText.IsDocument(title))
        {
            return null;
        }

        string trimmed = title.Trim();
        return trimmed.Length > CanonSynth.SessionLabelMaxChars
            ? trimmed[..CanonSynth.SessionLabelMaxChars]
            : trimmed;
    }

    private static DateTimeOffset? ParseUtcTimestamp(string? raw) =>
        HarnessJson.TryParseTimestamp(raw);

    private static long? ReadOptionalNonNegLong(JsonElement obj, string key) =>
        TryReadLong(obj, key, out long val) ? Math.Max(0L, val) : null;

    private static bool TryReadLong(JsonElement obj, string key, out long value) =>
        HarnessJson.TryReadLong(obj, key, out value);

    private static bool TryReadInt(JsonElement obj, string key, out int value) =>
        HarnessJson.TryReadInt(obj, key, out value);

    private record SourcePathInfo(
        string FileStem,
        bool IsNestedSubagentFile,
        string? SubagentParentSessionId);

    private record UsageSnapshot(
        string? MessageId,
        string SessionId,
        string? Model,
        ClaudeTranscriptUsage Usage,
        string? Timestamp);

    private record MergedLineGroup(
        List<ClaudeTranscriptLine> Lines,
        string? MessageId,
        UsageSnapshot? Usage);

    private record ParsedPart(
        ContentBucket Bucket,
        string Text,
        string? Server,
        string? Role);

    private record ParsedToolCall(
        string Id,
        string Name,
        string? RawInputText,
        bool InputIsString,
        string? Timestamp);

    private record ParsedToolResult(
        string ToolCallId,
        string? Content,
        bool IsError,
        string? Timestamp,
        long? ExitCode,
        string? ErrorCode);

    private record ParsedTurnGroup(
        string? SessionId,
        string? NativeRecordId,
        string? RequestId,
        string Timestamp,
        string? Model,
        ClaudeTranscriptUsage? Usage,
        string? SessionTitle,
        string? TerminationReason,
        int? ExitCode,
        string? CorrectionRule,
        string? AgentInstanceId,
        IReadOnlyList<ParsedPart> Parts,
        IReadOnlyList<ParsedToolCall> ToolCalls,
        IReadOnlyList<ParsedToolResult> ToolResults);
}
