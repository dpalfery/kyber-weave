using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Dash;

/// <summary>Result of parsing a single Claude Code turn group from a JSONL transcript.</summary>
public sealed record ClaudeTurnGroup(
    string? SessionId,
    string? NativeRecordId,
    string Timestamp,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    ReaderTurn Turn,
    string? SessionTitle = null);

/// <summary>
/// Streaming JSONL transcript readers for Claude Code and Codex session files
/// (<c>dash/src/synth/readers/claude.ts</c> and <c>dash/src/synth/readers/codex.ts</c>)
/// reading one line at a time without loading whole files into memory.
/// </summary>
public static class CanonTranscriptReaders
{
    private const long MergeWindowMs = 60_000;

    /// <summary>Streams Claude Code JSONL transcript lines from <paramref name="filePath"/> into merged turns.</summary>
    public static IReadOnlyList<ClaudeTurnGroup> ReadClaudeTurns(
        string filePath,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            return [];

        string fileStem = Path.GetFileNameWithoutExtension(filePath);
        List<List<string>> rawGroups = [];
        List<string> currentGroup = [];

        using (StreamReader reader = new(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                currentGroup.Add(line);
                if (TryParseClaudeUsage(line, fileStem) is not null)
                {
                    rawGroups.Add(currentGroup);
                    currentGroup = [];
                }
            }
        }

        if (currentGroup.Count > 0)
            rawGroups.Add(currentGroup);

        List<(List<string> Lines, string? MessageId, ClaudeUsageLine? FirstUsage)> mergedGroups = MergeClaudeGroups(rawGroups, fileStem);
        List<ClaudeTurnGroup> results = [];

        foreach ((List<string> lines, string? _, ClaudeUsageLine? firstUsage) in mergedGroups)
        {
            if (windowStart.HasValue || windowEnd.HasValue)
            {
                DateTimeOffset? instant = FirstGroupTimestamp(lines);
                if (instant is null)
                    continue;
                if (windowStart.HasValue && instant.Value < windowStart.Value)
                    continue;
                if (windowEnd.HasValue && instant.Value > windowEnd.Value)
                    continue;
            }

            ClaudeTurnGroup? parsed = ParseClaudeGroup(lines, firstUsage, fileStem);
            if (parsed is not null)
                results.Add(parsed);
        }

        return results;
    }

    /// <summary>
    /// Streams a Claude Code JSONL transcript into classified <see cref="SourceRecordEnvelope"/> items
    /// ready for <see cref="CanonSynth.SynthesizeEnvelopes"/>.
    /// </summary>
    public static IReadOnlyList<SourceRecordEnvelope> ReadClaudeEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        IReadOnlyList<ClaudeTurnGroup> groups = ReadClaudeTurns(filePath, windowStart, windowEnd);
        string resolvedSourceKey = !string.IsNullOrWhiteSpace(sourceKey) ? sourceKey : Path.GetFullPath(filePath);
        string resolvedImportedAt = importedAt ?? DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        List<SourceRecordEnvelope> envelopes = [];
        int index = 0;
        foreach (ClaudeTurnGroup group in groups)
        {
            index++;
            bool hasUsage = group.InputTokens != 0 || group.OutputTokens != 0 || group.CacheReadTokens != 0 || group.CacheCreationTokens != 0;
            if (!hasUsage)
                continue;

            string sessionId = group.SessionId ?? Path.GetFileNameWithoutExtension(filePath);
            string dedupSuffix = group.NativeRecordId ?? index.ToString(CultureInfo.InvariantCulture);
            ParsedProviderCall call = new()
            {
                Timestamp = group.Timestamp,
                Provider = "claude",
                Model = group.Model,
                InputTokens = group.InputTokens,
                OutputTokens = group.OutputTokens,
                CacheReadInputTokens = group.CacheReadTokens,
                CacheCreationInputTokens = group.CacheCreationTokens,
                SessionId = sessionId,
                DeduplicationKey = $"claude:{sessionId}:{dedupSuffix}",
                TurnId = group.NativeRecordId,
                SessionTitle = group.SessionTitle,
            };

            envelopes.Add(new SourceRecordEnvelope
            {
                HarnessId = "claude-code",
                SourceKey = resolvedSourceKey,
                NativeSessionId = sessionId,
                NativeRecordId = group.NativeRecordId,
                SourceRevision = sourceRevision,
                Format = "claude-jsonl",
                ParserId = "claude-transcript-reader",
                ParserContractVersion = "1",
                ImportedAt = resolvedImportedAt,
                Call = call,
                ReaderTurn = group.Turn,
            });
        }

        return envelopes;
    }

    /// <summary>Streams a Codex JSONL transcript from <paramref name="filePath"/> into cumulative snapshot <see cref="ReaderTurn"/> items.</summary>
    public static IReadOnlyList<ReaderTurn> ReadCodexTurns(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            return [];

        return ReadCodexInternal(filePath).Turns;
    }

    /// <summary>
    /// Streams a Codex JSONL transcript into classified <see cref="SourceRecordEnvelope"/> items
    /// ready for <see cref="CanonSynth.SynthesizeEnvelopes"/>.
    /// </summary>
    public static IReadOnlyList<SourceRecordEnvelope> ReadCodexEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            return [];

        (IReadOnlyList<ReaderTurn> _, IReadOnlyList<SourceRecordEnvelope> envelopes) =
            ReadCodexInternal(filePath, sourceKey, sourceRevision, importedAt);
        return envelopes;
    }

    private sealed record ClaudeUsageLine(
        string? MessageId,
        string SessionId,
        string Model,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheCreationTokens,
        string? Timestamp);

    private static ClaudeUsageLine? TryParseClaudeUsage(string rawLine, string fileStem)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawLine);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!doc.RootElement.TryGetProperty("message", out JsonElement msgEl) || msgEl.ValueKind != JsonValueKind.Object)
                return null;

            if (!msgEl.TryGetProperty("usage", out JsonElement usageEl) || usageEl.ValueKind != JsonValueKind.Object)
                return null;

            string? messageId = ReadString(msgEl, "id");
            string sessionId = ReadString(doc.RootElement, "sessionId") ?? fileStem;
            string model = ReadString(msgEl, "model") ?? "unknown";
            string? timestamp = ReadString(doc.RootElement, "timestamp");

            return new ClaudeUsageLine(
                MessageId: messageId,
                SessionId: sessionId,
                Model: model,
                InputTokens: ReadLong(usageEl, "input_tokens"),
                OutputTokens: ReadLong(usageEl, "output_tokens"),
                CacheReadTokens: ReadLong(usageEl, "cache_read_input_tokens"),
                CacheCreationTokens: ReadLong(usageEl, "cache_creation_input_tokens"),
                Timestamp: timestamp);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsMatchingClaudeUsage(ClaudeUsageLine? prev, ClaudeUsageLine? next)
    {
        if (prev is null || next is null)
            return false;

        if (prev.MessageId is not null && next.MessageId is not null && !string.Equals(prev.MessageId, next.MessageId, StringComparison.Ordinal))
            return false;

        if (!string.Equals(prev.SessionId, next.SessionId, StringComparison.Ordinal))
            return false;

        if (!string.Equals(prev.Model, next.Model, StringComparison.Ordinal))
            return false;

        if (prev.InputTokens != next.InputTokens
            || prev.OutputTokens != next.OutputTokens
            || prev.CacheReadTokens != next.CacheReadTokens
            || prev.CacheCreationTokens != next.CacheCreationTokens)
        {
            return false;
        }

        if (prev.InputTokens == 0 && prev.OutputTokens == 0 && prev.CacheReadTokens == 0 && prev.CacheCreationTokens == 0)
            return false;

        string prevTs = prev.Timestamp ?? "1970-01-01T00:00:00.000Z";
        string nextTs = next.Timestamp ?? "1970-01-01T00:00:00.000Z";
        if (DateTimeOffset.TryParse(prevTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset prevDt)
            && DateTimeOffset.TryParse(nextTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset nextDt))
        {
            if (Math.Abs((nextDt - prevDt).TotalMilliseconds) > MergeWindowMs)
                return false;
        }

        return true;
    }

    private static List<(List<string> Lines, string? MessageId, ClaudeUsageLine? FirstUsage)> MergeClaudeGroups(
        List<List<string>> rawGroups,
        string fileStem)
    {
        List<(List<string> Lines, string? MessageId, ClaudeUsageLine? FirstUsage)> merged = [];
        if (rawGroups.Count == 0)
            return merged;

        foreach (List<string> group in rawGroups)
        {
            string lastLine = group[^1];
            ClaudeUsageLine? usageInfo = TryParseClaudeUsage(lastLine, fileStem);

            if (merged.Count > 0)
            {
                (List<string> prevLines, string? prevMsgId, ClaudeUsageLine? prevUsage) = merged[^1];
                ClaudeUsageLine? prevForMatch = prevUsage is null
                    ? null
                    : prevUsage with { MessageId = prevMsgId ?? prevUsage.MessageId };
                if (IsMatchingClaudeUsage(prevForMatch, usageInfo))
                {
                    prevLines.AddRange(group);
                    if (prevMsgId is null && usageInfo?.MessageId is not null)
                        merged[^1] = (prevLines, usageInfo.MessageId, prevUsage);
                    continue;
                }
            }

            merged.Add(([.. group], usageInfo?.MessageId, usageInfo));
        }

        return merged;
    }

    private static DateTimeOffset? FirstGroupTimestamp(List<string> lines)
    {
        foreach (string line in lines)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                if (!doc.RootElement.TryGetProperty("message", out JsonElement msgEl) || !msgEl.TryGetProperty("usage", out _))
                    continue;

                string? rawTs = ReadString(doc.RootElement, "timestamp");
                if (rawTs is not null && DateTimeOffset.TryParse(rawTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dt))
                    return dt;

                return null;
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static ClaudeTurnGroup? ParseClaudeGroup(
        List<string> lines,
        ClaudeUsageLine? usage,
        string fileStem)
    {
        string? discoveredSessionId = null;
        string? nativeRecordId = usage?.MessageId;
        string? sessionTitle = null;
        string? terminationReason = null;
        int? exitCode = null;
        bool hasCorrection = false;
        string? correctionRule = null;
        List<ContentPart> parts = [];
        List<ReaderToolCall> toolCalls = [];
        List<ReaderToolResult> toolResults = [];
        int order = 0;

        foreach (string line in lines)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                JsonElement record = doc.RootElement;
                if (discoveredSessionId is null)
                    discoveredSessionId = ReadString(record, "sessionId");

                if (sessionTitle is null)
                    sessionTitle = ReadString(record, "customTitle") ?? ReadString(record, "aiTitle");

                if (TryReadInt(record, "exitCode", out int ec))
                    exitCode = ec;
                if (TryReadInt(record, "exit_code", out int ecSnake))
                    exitCode = ecSnake;

                string? term = ReadString(record, "terminationReason") ?? ReadString(record, "stop_reason");
                if (term is not null)
                    terminationReason = term;

                string? recType = ReadString(record, "type");
                if (recType is "exit" or "result")
                {
                    terminationReason = recType;
                    if (TryReadInt(record, "code", out int code))
                        exitCode = code;
                    if (TryReadInt(record, "exitCode", out int recEc))
                        exitCode = recEc;
                }

                string? server = ReadString(record, "attributionMcpServer")?.Trim();
                if (string.IsNullOrEmpty(server))
                    server = null;

                string? lineTimestamp = ReadString(record, "timestamp");

                if (!record.TryGetProperty("message", out JsonElement msgEl) || msgEl.ValueKind != JsonValueKind.Object)
                    continue;

                if (nativeRecordId is null && msgEl.TryGetProperty("usage", out _))
                    nativeRecordId = ReadString(msgEl, "id");

                string? role = ReadString(msgEl, "role");
                if (!msgEl.TryGetProperty("content", out JsonElement contentEl))
                    continue;

                if (contentEl.ValueKind == JsonValueKind.String)
                {
                    string? text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (string.Equals(role, "user", StringComparison.Ordinal))
                        {
                            CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(text);
                            if (check.Matched)
                            {
                                hasCorrection = true;
                                correctionRule = check.Rule;
                            }
                        }

                        parts.Add(new ContentPart("conversation_history", text, Server: server, Order: order++));
                    }

                    continue;
                }

                if (contentEl.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement block in contentEl.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object)
                        continue;

                    string? blockType = ReadString(block, "type");
                    if (string.Equals(blockType, "text", StringComparison.Ordinal))
                    {
                        string? text = ReadString(block, "text");
                        if (string.IsNullOrEmpty(text))
                            continue;

                        if (string.Equals(role, "user", StringComparison.Ordinal))
                        {
                            CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(text);
                            if (check.Matched)
                            {
                                hasCorrection = true;
                                correctionRule = check.Rule;
                            }
                        }

                        parts.Add(new ContentPart("conversation_history", text, Server: server, Order: order++));
                    }
                    else if (string.Equals(blockType, "thinking", StringComparison.Ordinal))
                    {
                        string? text = ReadString(block, "thinking") ?? ReadString(block, "text");
                        if (!string.IsNullOrEmpty(text))
                            parts.Add(new ContentPart("conversation_history", text, Server: server, Order: order++));
                    }
                    else if (string.Equals(blockType, "tool_result", StringComparison.Ordinal))
                    {
                        string text = ExtractClaudeToolResultText(block);
                        string toolCallId = ReadString(block, "tool_use_id") ?? ReadString(block, "id") ?? string.Empty;
                        if (!string.IsNullOrEmpty(toolCallId))
                        {
                            bool isError = (block.TryGetProperty("is_error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.True)
                                || (TryReadInt(block, "exit_code", out int toolEc1) && toolEc1 != 0)
                                || (TryReadInt(block, "exitCode", out int toolEc2) && toolEc2 != 0);

                            toolResults.Add(new ReaderToolResult
                            {
                                ToolCallId = toolCallId,
                                Content = text,
                                IsError = isError,
                                Timestamp = lineTimestamp,
                            });
                        }

                        if (!string.IsNullOrEmpty(text))
                            parts.Add(new ContentPart("tool_result_content", text, Server: server, Order: order++));
                    }
                    else if (string.Equals(blockType, "tool_use", StringComparison.Ordinal))
                    {
                        string id = ReadString(block, "id") ?? string.Empty;
                        string name = ReadString(block, "name") ?? string.Empty;
                        if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
                        {
                            JsonElement? argObj = null;
                            string? rawArgStr = null;
                            if (block.TryGetProperty("input", out JsonElement inputEl))
                            {
                                if (inputEl.ValueKind == JsonValueKind.String)
                                    rawArgStr = inputEl.GetString();
                                else if (inputEl.ValueKind == JsonValueKind.Object)
                                    argObj = inputEl.Clone();
                            }

                            toolCalls.Add(new ReaderToolCall
                            {
                                Id = id,
                                Name = name,
                                Arguments = argObj ?? JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
                                RawArgumentsString = rawArgStr,
                                Timestamp = lineTimestamp,
                            });
                        }
                    }
                }
            }
        }

        if (parts.Count == 0 && discoveredSessionId is null && toolCalls.Count == 0 && toolResults.Count == 0 && usage is null)
            return null;

        string sessionId = discoveredSessionId ?? fileStem;
        ReaderTurn turn = new()
        {
            Parts = parts,
            SessionId = sessionId,
            NativeRecordId = nativeRecordId,
            TerminationReason = terminationReason,
            ExitCode = exitCode,
            IsCorrection = hasCorrection ? true : null,
            CorrectionRule = correctionRule,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            ToolResults = toolResults.Count > 0 ? toolResults : null,
        };

        return new ClaudeTurnGroup(
            SessionId: sessionId,
            NativeRecordId: nativeRecordId,
            Timestamp: usage?.Timestamp ?? "1970-01-01T00:00:00.000Z",
            Model: usage?.Model ?? "unknown",
            InputTokens: usage?.InputTokens ?? 0,
            OutputTokens: usage?.OutputTokens ?? 0,
            CacheReadTokens: usage?.CacheReadTokens ?? 0,
            CacheCreationTokens: usage?.CacheCreationTokens ?? 0,
            Turn: turn,
            SessionTitle: sessionTitle);
    }

    private static (IReadOnlyList<ReaderTurn> Turns, IReadOnlyList<SourceRecordEnvelope> Envelopes) ReadCodexInternal(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null)
    {
        string fileStem = Path.GetFileNameWithoutExtension(filePath);
        string resolvedSourceKey = !string.IsNullOrWhiteSpace(sourceKey) ? sourceKey : Path.GetFullPath(filePath);
        string resolvedImportedAt = importedAt ?? DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        string? systemPrompt = null;
        string? instructionContext = null;
        List<ContentPart> items = [];
        List<ReaderToolCall> pendingCalls = [];
        List<ReaderToolResult> pendingResults = [];
        string? sessionId = null;
        long? contextWindow = null;
        string? terminationReason = null;
        int? exitCode = null;
        bool? isCorrection = null;
        string? correctionRule = null;
        string currentModel = "codex";
        bool dirty = false;
        bool yielded = false;
        int turnIndex = 0;

        List<ReaderTurn> turns = [];
        List<SourceRecordEnvelope> envelopes = [];

        ReaderTurn? BuildSnapshotIfNonEmpty()
        {
            List<ContentPart> snapshotParts = [];
            int order = 0;
            if (systemPrompt is not null)
                snapshotParts.Add(new ContentPart("system_prompt", systemPrompt, Order: order++));
            if (instructionContext is not null)
                snapshotParts.Add(new ContentPart("instruction_context", instructionContext, Order: order++));
            foreach (ContentPart item in items)
                snapshotParts.Add(item with { Order = order++ });

            if (snapshotParts.Count == 0
                && sessionId is null
                && !contextWindow.HasValue
                && terminationReason is null
                && !exitCode.HasValue
                && !isCorrection.HasValue
                && pendingCalls.Count == 0
                && pendingResults.Count == 0)
            {
                return null;
            }

            ReaderTurn turn = new()
            {
                Parts = snapshotParts,
                SessionId = sessionId,
                ContextWindow = contextWindow,
                TerminationReason = terminationReason,
                ExitCode = exitCode,
                IsCorrection = isCorrection,
                CorrectionRule = correctionRule,
                ToolCalls = pendingCalls.Count > 0 ? [.. pendingCalls] : null,
                ToolResults = pendingResults.Count > 0 ? [.. pendingResults] : null,
            };

            dirty = false;
            yielded = true;
            isCorrection = null;
            correctionRule = null;
            pendingCalls.Clear();
            pendingResults.Clear();
            return turn;
        }

        using (StreamReader reader = new(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        continue;

                    JsonElement entry = doc.RootElement;
                    string? entryType = ReadString(entry, "type");
                    string? entryTimestamp = ReadString(entry, "timestamp");
                    if (!entry.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
                        continue;

                    if (string.Equals(entryType, "session_meta", StringComparison.Ordinal))
                    {
                        string? sid = ReadString(payload, "session_id");
                        if (!string.IsNullOrEmpty(sid))
                        {
                            sessionId = sid;
                            dirty = true;
                        }

                        string? prompt = ReadCodexSystemPrompt(payload);
                        if (!string.IsNullOrEmpty(prompt))
                        {
                            systemPrompt = prompt;
                            dirty = true;
                        }

                        string? modelFromMeta = ReadString(payload, "model");
                        if (!string.IsNullOrEmpty(modelFromMeta))
                            currentModel = modelFromMeta;

                        continue;
                    }

                    if (string.Equals(entryType, "world_state", StringComparison.Ordinal))
                    {
                        string? agentsMd = ReadCodexAgentsMd(payload);
                        if (!string.IsNullOrEmpty(agentsMd))
                        {
                            instructionContext = agentsMd;
                            dirty = true;
                        }

                        continue;
                    }

                    if (string.Equals(entryType, "event_msg", StringComparison.Ordinal))
                    {
                        long? win = ReadCodexContextWindow(payload);
                        if (win.HasValue)
                            contextWindow = win.Value;

                        string? eventType = ReadString(payload, "type");
                        if (string.Equals(eventType, "task_complete", StringComparison.Ordinal))
                        {
                            terminationReason = "task_complete";
                            exitCode = 0;
                            dirty = true;
                        }
                        else if (eventType is "task_failed" or "error")
                        {
                            terminationReason = eventType;
                            exitCode = TryReadInt(payload, "exit_code", out int failCode) ? failCode : 1;
                            dirty = true;
                        }
                        else if (eventType is "turn_aborted" or "cancelled")
                        {
                            terminationReason = "aborted";
                            dirty = true;
                        }
                        else if (string.Equals(eventType, "token_count", StringComparison.Ordinal))
                        {
                            turnIndex++;
                            ReaderTurn? flushed = BuildSnapshotIfNonEmpty();
                            if (flushed is not null)
                                turns.Add(flushed);

                            if (TryExtractCodexUsage(payload, entryTimestamp, sessionId ?? fileStem, currentModel, turnIndex, out ParsedProviderCall? call, out string? detectedModel))
                            {
                                if (!string.IsNullOrEmpty(detectedModel))
                                    currentModel = detectedModel;

                                string nativeSid = sessionId ?? fileStem;
                                envelopes.Add(new SourceRecordEnvelope
                                {
                                    HarnessId = "codex-cli",
                                    SourceKey = resolvedSourceKey,
                                    NativeSessionId = nativeSid,
                                    SourceRevision = sourceRevision,
                                    Format = "codex-jsonl",
                                    ParserId = "codex-transcript-reader",
                                    ParserContractVersion = "1",
                                    ImportedAt = resolvedImportedAt,
                                    Call = call!,
                                    ReaderTurn = flushed,
                                });
                            }
                        }

                        continue;
                    }

                    if (string.Equals(entryType, "response_item", StringComparison.Ordinal))
                    {
                        string? itemType = ReadString(payload, "type");
                        if (string.Equals(itemType, "message", StringComparison.Ordinal))
                        {
                            string? text = ReadCodexMessageText(payload);
                            if (!string.IsNullOrEmpty(text))
                            {
                                CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(text);
                                if (check.Matched)
                                {
                                    isCorrection = true;
                                    correctionRule = check.Rule;
                                }

                                items.Add(new ContentPart("conversation_history", text));
                                dirty = true;
                            }
                        }
                        else if (itemType is "function_call" or "custom_tool_call")
                        {
                            string id = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? string.Empty;
                            string name = ReadString(payload, "name") ?? string.Empty;
                            if (!string.IsNullOrEmpty(name))
                            {
                                string? rawArgs = ReadString(payload, "arguments") ?? ReadString(payload, "input");
                                pendingCalls.Add(new ReaderToolCall
                                {
                                    Id = id,
                                    Name = name,
                                    RawArgumentsString = rawArgs ?? "{}",
                                    Timestamp = entryTimestamp,
                                });
                                dirty = true;
                            }
                        }
                        else if (itemType is "function_call_output" or "custom_tool_call_output")
                        {
                            string? outputText = ReadCodexToolResultText(payload);
                            if (!string.IsNullOrEmpty(outputText))
                            {
                                items.Add(new ContentPart("tool_result_content", outputText));
                                string callId = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? string.Empty;
                                if (!string.IsNullOrEmpty(callId))
                                {
                                    pendingResults.Add(new ReaderToolResult
                                    {
                                        ToolCallId = callId,
                                        Content = outputText,
                                        IsError = false,
                                        Timestamp = entryTimestamp,
                                    });
                                }

                                dirty = true;
                            }
                        }
                    }
                }
            }
        }

        if (dirty || !yielded)
        {
            ReaderTurn? trailing = BuildSnapshotIfNonEmpty();
            if (trailing is not null)
                turns.Add(trailing);
        }

        return (turns, envelopes);
    }

    private static string ExtractClaudeToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out JsonElement rawContent))
            return ReadString(block, "text") ?? block.GetRawText();

        if (rawContent.ValueKind == JsonValueKind.String)
            return rawContent.GetString() ?? string.Empty;

        if (rawContent.ValueKind == JsonValueKind.Array)
        {
            List<string> lines = [];
            foreach (JsonElement item in rawContent.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    lines.Add(item.GetString() ?? string.Empty);
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    string? t = ReadString(item, "text");
                    lines.Add(t ?? item.GetRawText());
                }
                else
                {
                    lines.Add(item.GetRawText());
                }
            }

            return string.Join('\n', lines);
        }

        if (rawContent.ValueKind == JsonValueKind.Object)
            return ReadString(rawContent, "text") ?? rawContent.GetRawText();

        return ReadString(block, "text") ?? block.GetRawText();
    }

    private static string? ReadCodexSystemPrompt(JsonElement payload)
    {
        string? direct = ReadString(payload, "system_prompt");
        if (!string.IsNullOrEmpty(direct))
            return direct;

        if (payload.TryGetProperty("base_instructions", out JsonElement baseEl) && baseEl.ValueKind == JsonValueKind.Object)
            return ReadString(baseEl, "text");

        return null;
    }

    private static string? ReadCodexAgentsMd(JsonElement payload)
    {
        if (!payload.TryGetProperty("state", out JsonElement stateEl) || stateEl.ValueKind != JsonValueKind.Object)
            return null;

        if (!stateEl.TryGetProperty("agents_md", out JsonElement agentsEl) || agentsEl.ValueKind != JsonValueKind.Object)
            return null;

        return ReadString(agentsEl, "text");
    }

    private static long? ReadCodexContextWindow(JsonElement payload)
    {
        if (TryReadLong(payload, "model_context_window", out long direct))
            return direct;

        if (payload.TryGetProperty("info", out JsonElement infoEl)
            && infoEl.ValueKind == JsonValueKind.Object
            && TryReadLong(infoEl, "model_context_window", out long nested))
        {
            return nested;
        }

        return null;
    }

    private static string? ReadCodexMessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out JsonElement content))
            return null;

        if (content.ValueKind == JsonValueKind.String)
        {
            string? text = content.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        if (content.ValueKind != JsonValueKind.Array)
            return null;

        List<string> texts = [];
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;

            string? text = ReadString(block, "text");
            if (!string.IsNullOrEmpty(text))
                texts.Add(text);
        }

        return texts.Count > 0 ? string.Join('\n', texts) : null;
    }

    private static string? ReadCodexToolResultText(JsonElement payload)
    {
        if (!payload.TryGetProperty("output", out JsonElement output))
            return null;

        if (output.ValueKind == JsonValueKind.String)
        {
            string? text = output.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        if (output.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return output.GetRawText();

        return null;
    }

    private static bool TryExtractCodexUsage(
        JsonElement payload,
        string? timestamp,
        string sessionId,
        string fallbackModel,
        int turnIndex,
        out ParsedProviderCall? call,
        out string? detectedModel)
    {
        call = null;
        detectedModel = null;

        JsonElement usageContainer = default;
        bool hasUsage = false;
        if (payload.TryGetProperty("info", out JsonElement infoEl) && infoEl.ValueKind == JsonValueKind.Object)
        {
            detectedModel = ReadString(infoEl, "model");
            if (infoEl.TryGetProperty("last_token_usage", out JsonElement lastUsage) && lastUsage.ValueKind == JsonValueKind.Object)
            {
                usageContainer = lastUsage;
                hasUsage = true;
            }
            else if (infoEl.TryGetProperty("total_token_usage", out JsonElement totalUsage) && totalUsage.ValueKind == JsonValueKind.Object)
            {
                usageContainer = totalUsage;
                hasUsage = true;
            }
        }
        else if (payload.TryGetProperty("usage", out JsonElement directUsage) && directUsage.ValueKind == JsonValueKind.Object)
        {
            usageContainer = directUsage;
            hasUsage = true;
        }

        if (!hasUsage)
            return false;

        long rawInput = ReadLong(usageContainer, "input_tokens");
        long cached = ReadLong(usageContainer, "cached_input_tokens");
        long fresh = Math.Max(0L, rawInput - cached);
        long output = ReadLong(usageContainer, "output_tokens");
        long reasoning = ReadLong(usageContainer, "reasoning_output_tokens");
        string model = detectedModel ?? ReadString(payload, "model") ?? fallbackModel;

        call = new ParsedProviderCall
        {
            Timestamp = timestamp ?? "1970-01-01T00:00:00.000Z",
            Provider = "codex",
            Model = model,
            InputTokens = fresh,
            OutputTokens = output,
            CacheReadInputTokens = cached,
            CachedInputTokens = cached,
            ReasoningTokens = reasoning,
            SessionId = sessionId,
            DeduplicationKey = string.Create(CultureInfo.InvariantCulture, $"codex:{sessionId}:turn-{turnIndex}"),
        };

        return true;
    }

    private static string? ReadString(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
            return null;

        string? val = el.GetString();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static long ReadLong(JsonElement obj, string key) =>
        TryReadLong(obj, key, out long val) ? val : 0L;

    private static bool TryReadLong(JsonElement obj, string key, out long value)
    {
        value = 0L;
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
            return false;

        if (el.TryGetInt64(out long i))
        {
            value = i;
            return true;
        }

        if (el.TryGetDouble(out double d) && double.IsFinite(d))
        {
            value = (long)d;
            return true;
        }

        return false;
    }

    private static bool TryReadInt(JsonElement obj, string key, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
            return false;

        return el.TryGetInt32(out value);
    }
}
