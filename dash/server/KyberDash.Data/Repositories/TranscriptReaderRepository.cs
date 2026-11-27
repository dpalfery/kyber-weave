using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses;
using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Streaming JSONL transcript readers for Claude Code and Codex session files, plus discovery of the
/// local harness transcript roots, so synthesis can be fed from the files a developer already has.
/// </summary>
public class TranscriptReaderRepository : ITranscriptReaderRepository
{
    /// <summary>Harness identifier of Claude Code transcripts.</summary>
    public const string ClaudeHarnessId = "claude-code";

    /// <summary>Harness identifier of Codex CLI transcripts.</summary>
    public const string CodexHarnessId = "codex-cli";

    /// <summary>Provider identifier reported for Claude Code calls.</summary>
    public const string ClaudeProviderId = "claude";

    /// <summary>Provider identifier reported for Codex calls.</summary>
    public const string CodexProviderId = "codex";

    /// <summary>Reader implementation identifier recorded in source provenance for Claude transcripts.</summary>
    public const string ClaudeParserId = "claude-transcript-reader";

    /// <summary>Reader implementation identifier recorded in source provenance for Codex transcripts.</summary>
    public const string CodexParserId = "codex-transcript-reader";

    /// <summary>Parser contract version shared by both readers; bumping it invalidates checkpoints.</summary>
    public const string ParserContractVersion = "1";

    /// <summary>Source format string for Claude Code JSONL transcripts.</summary>
    public const string ClaudeFormat = "claude-jsonl";

    /// <summary>Source format string for Codex JSONL transcripts.</summary>
    public const string CodexFormat = "codex-jsonl";

    private const long MergeWindowMs = 60_000;

    private readonly string homeDirectory;
    private readonly string stateDirectory;
    private readonly IHarnessFileSystem fileSystem;
    private readonly HarnessSourceRegistry sourceRegistry;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a reader that discovers sources under the current user's home directory.</summary>
    public TranscriptReaderRepository()
        : this(null, null, null)
    {
    }

    /// <summary>Initializes a reader whose source discovery starts from <paramref name="homeDirectory"/>.</summary>
    /// <param name="homeDirectory">Home directory holding <c>.claude</c> and <c>.codex</c>; defaults to the current user's.</param>
    public TranscriptReaderRepository(string? homeDirectory)
        : this(homeDirectory, null, null)
    {
    }

    /// <summary>Initializes a reader whose source discovery uses explicit home, filesystem seam, and optional state directory.</summary>
    /// <param name="homeDirectory">Home directory holding local harness roots; defaults to the current user's.</param>
    /// <param name="fileSystem">Filesystem seam used for discovery; defaults to <see cref="PhysicalHarnessFileSystem.Instance"/>.</param>
    /// <param name="stateDirectory">KyberDash state directory holding caches; defaults to <c>.kyberdash</c> under <paramref name="homeDirectory"/> when provided, otherwise <see cref="ServerStateRepository.ResolveStateDirectory"/>.</param>
    /// <param name="timeProvider">Clock seam for generated timestamps; defaults to <see cref="TimeProvider.System"/>.</param>
    public TranscriptReaderRepository(
        string? homeDirectory,
        IHarnessFileSystem? fileSystem,
        string? stateDirectory = null,
        TimeProvider? timeProvider = null)
    {
        bool explicitHome = !string.IsNullOrWhiteSpace(homeDirectory);
        this.homeDirectory = explicitHome
            ? Path.GetFullPath(homeDirectory!)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.stateDirectory = !string.IsNullOrWhiteSpace(stateDirectory)
            ? Path.GetFullPath(stateDirectory)
            : explicitHome
                ? Path.Combine(this.homeDirectory, CanonDbPath.DefaultDirectoryName)
                : ServerStateRepository.ResolveStateDirectory();
        this.fileSystem = fileSystem ?? PhysicalHarnessFileSystem.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        sourceRegistry = new HarnessSourceRegistry(HarnessSourceRegistry.CreateSqliteSource(this.stateDirectory));
    }

    /// <inheritdoc />
    public IReadOnlyList<ClaudeTurnGroup> ReadClaudeTurns(
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

        try
        {
            using StreamReader reader = new(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
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
        catch (IOException)
        {
            return [];
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

    /// <inheritdoc />
    public IReadOnlyList<SourceRecordEnvelope> ReadClaudeEnvelopes(
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
        string resolvedImportedAt = importedAt ?? NowIso();

        List<SourceRecordEnvelope> envelopes = [];
        int index = 0;
        foreach (ClaudeTurnGroup group in groups)
        {
            index++;
            bool hasUsage = group.InputTokens != 0 || group.OutputTokens != 0 || group.CacheReadTokens != 0 || group.CacheCreationTokens != 0;
            if (!hasUsage)
            {
                // A transcript tail (tool calls and results after the last usage line) belongs to
                // the call before it, so merge everything it carries there rather than dropping it.
                if (envelopes.Count > 0 && HasTurnContent(group.Turn))
                {
                    SourceRecordEnvelope previous = envelopes[^1];
                    if (previous.ReaderTurn is { } previousTurn)
                    {
                        envelopes[^1] = previous with
                        {
                            ReaderTurn = MergeTailTurn(previousTurn, group.Turn),
                        };
                    }
                }

                continue;
            }

            string sessionId = group.SessionId ?? Path.GetFileNameWithoutExtension(filePath);
            string dedupSuffix = group.NativeRecordId ?? index.ToString(CultureInfo.InvariantCulture);
            ParsedProviderCall call = new()
            {
                Timestamp = group.Timestamp,
                Provider = ClaudeProviderId,
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
                HarnessId = ClaudeHarnessId,
                SourceKey = resolvedSourceKey,
                NativeSessionId = sessionId,
                NativeRecordId = group.NativeRecordId,
                SourceRevision = sourceRevision,
                Format = ClaudeFormat,
                ParserId = ClaudeParserId,
                ParserContractVersion = HarnessSourceRegistry.ClaudeTranscriptV2ParserContractVersion,
                ImportedAt = resolvedImportedAt,
                Call = call,
                ReaderTurn = group.Turn,
            });
        }

        return envelopes;
    }

    /// <inheritdoc />
    public IReadOnlyList<ReaderTurn> ReadCodexTurns(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            return [];

        return ReadCodexInternal(filePath).Turns;
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceRecordEnvelope> ReadCodexEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            return [];

        (IReadOnlyList<ReaderTurn> _, IReadOnlyList<SourceRecordEnvelope> envelopes) =
            ReadCodexInternal(filePath, sourceKey, sourceRevision, importedAt);

        if (windowStart is null && windowEnd is null)
            return envelopes;

        List<SourceRecordEnvelope> filtered = [];
        foreach (SourceRecordEnvelope envelope in envelopes)
        {
            if (!DateTimeOffset.TryParse(envelope.Call.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset instant))
                continue;
            if (windowStart.HasValue && instant < windowStart.Value)
                continue;
            if (windowEnd.HasValue && instant > windowEnd.Value)
                continue;

            filtered.Add(envelope);
        }

        return filtered;
    }

    /// <inheritdoc />
    public IReadOnlyList<RefreshSourceInput> DiscoverLocalSources(DateTimeOffset? sinceUtc = null)
    {
        SourceDiscoveryContext context = new(homeDirectory, stateDirectory, fileSystem, sinceUtc);
        SourceDiscoveryResult discovery = sourceRegistry.DiscoverAll(context);
        List<RefreshSourceInput> sources = new(discovery.Sources.Count);

        foreach (SourceUnit unit in discovery.Sources)
        {
            Func<IReadOnlyList<SourceRecordEnvelope>> loader = unit.HarnessId switch
            {
                ClaudeHarnessId => () => ReadClaudeEnvelopes(unit.SourceKey, unit.SourceKey, unit.RevisionToken, null, null, null),
                CodexHarnessId => () => ReadCodexEnvelopes(unit.SourceKey, unit.SourceKey, unit.RevisionToken, null, null, null),
                _ => static () => [],
            };

            sources.Add(new RefreshSourceInput
            {
                HarnessId = unit.HarnessId,
                SourceKey = unit.SourceKey,
                ProviderId = unit.ProviderId,
                ParserId = unit.ParserId,
                ParserContractVersion = unit.ParserContractVersion,
                Format = unit.Format,
                SourceRootLabel = unit.SourceRootLabel,
                RevisionToken = unit.RevisionToken,
                LoadEnvelopes = loader,
            });
        }

        return sources;
    }

    private string NowIso() =>
        timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>True when a usage-less turn still carries content worth keeping.</summary>
    private static bool HasTurnContent(ReaderTurn turn) =>
        turn.Parts.Count > 0 || turn.ToolCalls is { Count: > 0 } || turn.ToolResults is { Count: > 0 };

    /// <summary>
    /// Folds a usage-less tail turn into the call before it: same call, more content, and the
    /// scalar fields the earlier turn already stated won.
    /// </summary>
    private static ReaderTurn MergeTailTurn(ReaderTurn head, ReaderTurn tail) =>
        head with
        {
            Parts = [.. head.Parts, .. tail.Parts],
            ToolCalls = Concat(head.ToolCalls, tail.ToolCalls),
            ToolResults = Concat(head.ToolResults, tail.ToolResults),
            ToolsOffered = ConcatDistinct(head.ToolsOffered, tail.ToolsOffered),
        };

    private static IReadOnlyList<T>? Concat<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second)
    {
        if (first is null || first.Count == 0)
            return second is { Count: > 0 } ? second : first;
        if (second is null || second.Count == 0)
            return first;

        return [.. first, .. second];
    }

    private static IReadOnlyList<string>? ConcatDistinct(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
    {
        if (first is null || first.Count == 0)
            return second is { Count: > 0 } ? second : first;
        if (second is null || second.Count == 0)
            return first;

        return [.. first.Concat(second).Distinct(StringComparer.Ordinal)];
    }

    private record ClaudeUsageLine(
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

            string sessionId = ReadString(doc.RootElement, "sessionId") ?? fileStem;
            return new ClaudeUsageLine(
                MessageId: ReadString(msgEl, "id"),
                SessionId: sessionId,
                Model: ReadString(msgEl, "model") ?? "unknown",
                InputTokens: ReadLong(usageEl, "input_tokens"),
                OutputTokens: ReadLong(usageEl, "output_tokens"),
                CacheReadTokens: ReadLong(usageEl, "cache_read_input_tokens"),
                CacheCreationTokens: ReadLong(usageEl, "cache_creation_input_tokens"),
                Timestamp: ReadString(doc.RootElement, "timestamp"));
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
                return rawTs is not null
                    && DateTimeOffset.TryParse(rawTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dt)
                        ? dt
                        : null;
            }
            catch (JsonException)
            {
                // Skip an unparseable line and keep looking for the usage line.
            }
        }

        return null;
    }

    private static ClaudeTurnGroup? ParseClaudeGroup(
        List<string> lines,
        ClaudeUsageLine? usage,
        string fileStem)
    {
        ClaudeGroupBuilder builder = new(fileStem, usage);
        foreach (string line in lines)
        {
            builder.AddLine(line);
        }

        return builder.Build();
    }

    /// <summary>Accumulates one Claude transcript group's lines into parts, tool calls, and metadata.</summary>
    private sealed class ClaudeGroupBuilder
    {
        private readonly string fileStem;
        private readonly ClaudeUsageLine? usage;
        private readonly List<ContentPart> parts = [];
        private readonly List<ReaderToolCall> toolCalls = [];
        private readonly List<ReaderToolResult> toolResults = [];
        private string? discoveredSessionId;
        private string? nativeRecordId;
        private string? sessionTitle;
        private string? terminationReason;
        private int? exitCode;
        private bool hasCorrection;
        private string? correctionRule;
        private int order;

        internal ClaudeGroupBuilder(string fileStem, ClaudeUsageLine? usage)
        {
            this.fileStem = fileStem;
            this.usage = usage;
            nativeRecordId = usage?.MessageId;
        }

        internal void AddLine(string line)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    ProcessRecord(doc.RootElement);
                }
            }
        }

        internal ClaudeTurnGroup? Build()
        {
            if (parts.Count == 0 && discoveredSessionId is null && toolCalls.Count == 0 && toolResults.Count == 0 && usage is null)
            {
                return null;
            }

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

        private void ProcessRecord(JsonElement record)
        {
            discoveredSessionId ??= ReadString(record, "sessionId");
            sessionTitle ??= ReadString(record, "customTitle") ?? ReadString(record, "aiTitle");

            if (TryReadInt(record, "exitCode", out int ec))
            {
                exitCode = ec;
            }

            if (TryReadInt(record, "exit_code", out int ecSnake))
            {
                exitCode = ecSnake;
            }

            string? term = ReadString(record, "terminationReason") ?? ReadString(record, "stop_reason");
            if (term is not null)
            {
                terminationReason = term;
            }

            string? recType = ReadString(record, "type");
            if (recType is "exit" or "result")
            {
                terminationReason = recType;
                if (TryReadInt(record, "code", out int code))
                {
                    exitCode = code;
                }

                if (TryReadInt(record, "exitCode", out int recEc))
                {
                    exitCode = recEc;
                }
            }

            string? server = ReadString(record, "attributionMcpServer")?.Trim();
            if (string.IsNullOrEmpty(server))
            {
                server = null;
            }

            string? lineTimestamp = ReadString(record, "timestamp");

            if (!record.TryGetProperty("message", out JsonElement msgEl) || msgEl.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (nativeRecordId is null && msgEl.TryGetProperty("usage", out _))
            {
                nativeRecordId = ReadString(msgEl, "id");
            }

            string? role = ReadString(msgEl, "role");
            if (!msgEl.TryGetProperty("content", out JsonElement contentEl))
            {
                return;
            }

            ProcessContent(contentEl, role, server, lineTimestamp);
        }

        private void ProcessContent(JsonElement contentEl, string? role, string? server, string? lineTimestamp)
        {
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                AddConversationText(contentEl.GetString(), role, server);
                return;
            }

            if (contentEl.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement block in contentEl.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object)
                {
                    ProcessContentBlock(block, role, server, lineTimestamp);
                }
            }
        }

        private void ProcessContentBlock(JsonElement block, string? role, string? server, string? lineTimestamp)
        {
            string? blockType = ReadString(block, "type");
            if (string.Equals(blockType, "text", StringComparison.Ordinal))
            {
                AddConversationText(ReadString(block, "text"), role, server);
            }
            else if (string.Equals(blockType, "thinking", StringComparison.Ordinal))
            {
                string? text = ReadString(block, "thinking") ?? ReadString(block, "text");
                AddPartIfPresent("conversation_history", text, server);
            }
            else if (string.Equals(blockType, "tool_result", StringComparison.Ordinal))
            {
                AddToolResult(block, server, lineTimestamp);
            }
            else if (string.Equals(blockType, "tool_use", StringComparison.Ordinal))
            {
                AddToolUse(block, lineTimestamp);
            }
        }

        private void AddConversationText(string? text, string? role, string? server)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

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

        private void AddPartIfPresent(string bucket, string? text, string? server)
        {
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(new ContentPart(bucket, text, Server: server, Order: order++));
            }
        }

        private void AddToolResult(JsonElement block, string? server, string? lineTimestamp)
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

            AddPartIfPresent("tool_result_content", text, server);
        }

        private void AddToolUse(JsonElement block, string? lineTimestamp)
        {
            string id = ReadString(block, "id") ?? string.Empty;
            string name = ReadString(block, "name") ?? string.Empty;
            if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name))
            {
                return;
            }

            JsonElement? argObj = null;
            string? rawArgStr = null;
            if (block.TryGetProperty("input", out JsonElement inputEl))
            {
                if (inputEl.ValueKind == JsonValueKind.String)
                {
                    rawArgStr = inputEl.GetString();
                }
                else if (inputEl.ValueKind == JsonValueKind.Object)
                {
                    argObj = inputEl.Clone();
                }
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

    private (IReadOnlyList<ReaderTurn> Turns, IReadOnlyList<SourceRecordEnvelope> Envelopes) ReadCodexInternal(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null)
    {
        string fileStem = Path.GetFileNameWithoutExtension(filePath);
        string resolvedSourceKey = !string.IsNullOrWhiteSpace(sourceKey) ? sourceKey : Path.GetFullPath(filePath);
        string resolvedImportedAt = importedAt ?? NowIso();
        CodexTurnBuilder builder = new(fileStem, resolvedSourceKey, sourceRevision, resolvedImportedAt);

        try
        {
            using StreamReader reader = new(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    builder.AddLine(line);
                }
            }
        }
        catch (IOException)
        {
            return (builder.Turns, builder.Envelopes);
        }

        builder.Finish();
        return (builder.Turns, builder.Envelopes);
    }

    /// <summary>Accumulates one Codex transcript's turns and provider-call envelopes.</summary>
    private sealed class CodexTurnBuilder
    {
        private readonly string fileStem;
        private readonly string resolvedSourceKey;
        private readonly string? sourceRevision;
        private readonly string resolvedImportedAt;
        private readonly List<ContentPart> items = [];
        private readonly List<ReaderToolCall> pendingCalls = [];
        private readonly List<ReaderToolResult> pendingResults = [];
        private readonly List<ReaderTurn> turns = [];
        private readonly List<SourceRecordEnvelope> envelopes = [];
        private string? systemPrompt;
        private string? instructionContext;
        private string? sessionId;
        private long? contextWindow;
        private string? terminationReason;
        private int? exitCode;
        private bool? isCorrection;
        private string? correctionRule;
        private string currentModel = "codex";
        private bool dirty;
        private bool yielded;
        private int turnIndex;

        internal CodexTurnBuilder(string fileStem, string resolvedSourceKey, string? sourceRevision, string resolvedImportedAt)
        {
            this.fileStem = fileStem;
            this.resolvedSourceKey = resolvedSourceKey;
            this.sourceRevision = sourceRevision;
            this.resolvedImportedAt = resolvedImportedAt;
        }

        internal IReadOnlyList<ReaderTurn> Turns => turns;

        internal IReadOnlyList<SourceRecordEnvelope> Envelopes => envelopes;

        internal void AddLine(string line)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    ProcessEntry(doc.RootElement);
                }
            }
        }

        internal void Finish()
        {
            if (!dirty && yielded)
            {
                return;
            }

            ReaderTurn? trailing = BuildSnapshotIfNonEmpty();
            if (trailing is not null)
            {
                turns.Add(trailing);
            }
        }

        private void ProcessEntry(JsonElement entry)
        {
            string? entryType = ReadString(entry, "type");
            string? entryTimestamp = ReadString(entry, "timestamp");
            if (!entry.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (string.Equals(entryType, "session_meta", StringComparison.Ordinal))
            {
                ProcessSessionMeta(payload);
            }
            else if (string.Equals(entryType, "world_state", StringComparison.Ordinal))
            {
                ProcessWorldState(payload);
            }
            else if (string.Equals(entryType, "event_msg", StringComparison.Ordinal))
            {
                ProcessEventMessage(payload, entryTimestamp);
            }
            else if (string.Equals(entryType, "response_item", StringComparison.Ordinal))
            {
                ProcessResponseItem(payload, entryTimestamp);
            }
        }

        private void ProcessSessionMeta(JsonElement payload)
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
            {
                currentModel = modelFromMeta;
            }
        }

        private void ProcessWorldState(JsonElement payload)
        {
            string? agentsMd = ReadCodexAgentsMd(payload);
            if (!string.IsNullOrEmpty(agentsMd))
            {
                instructionContext = agentsMd;
                dirty = true;
            }
        }

        private void ProcessEventMessage(JsonElement payload, string? entryTimestamp)
        {
            long? win = ReadCodexContextWindow(payload);
            if (win.HasValue)
            {
                contextWindow = win.Value;
            }

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
                ProcessTokenCount(payload, entryTimestamp);
            }
        }

        private void ProcessTokenCount(JsonElement payload, string? entryTimestamp)
        {
            turnIndex++;
            ReaderTurn? flushed = BuildSnapshotIfNonEmpty();
            if (flushed is not null)
            {
                turns.Add(flushed);
            }

            if (!TryExtractCodexUsage(payload, entryTimestamp, sessionId ?? fileStem, currentModel, turnIndex, out ParsedProviderCall? call, out string? detectedModel))
            {
                return;
            }

            if (!string.IsNullOrEmpty(detectedModel))
            {
                currentModel = detectedModel;
            }

            string nativeSid = sessionId ?? fileStem;
            envelopes.Add(new SourceRecordEnvelope
            {
                HarnessId = CodexHarnessId,
                SourceKey = resolvedSourceKey,
                NativeSessionId = nativeSid,
                SourceRevision = sourceRevision,
                Format = CodexFormat,
                ParserId = CodexParserId,
                ParserContractVersion = ParserContractVersion,
                ImportedAt = resolvedImportedAt,
                Call = call!,
                ReaderTurn = flushed,
            });
        }

        private void ProcessResponseItem(JsonElement payload, string? entryTimestamp)
        {
            string? itemType = ReadString(payload, "type");
            if (string.Equals(itemType, "message", StringComparison.Ordinal))
            {
                ProcessMessageItem(payload);
            }
            else if (itemType is "function_call" or "custom_tool_call")
            {
                ProcessToolCallItem(payload, entryTimestamp);
            }
            else if (itemType is "function_call_output" or "custom_tool_call_output")
            {
                ProcessToolResultItem(payload, entryTimestamp);
            }
        }

        private void ProcessMessageItem(JsonElement payload)
        {
            string? text = ReadCodexMessageText(payload);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(text);
            if (check.Matched)
            {
                isCorrection = true;
                correctionRule = check.Rule;
            }

            items.Add(new ContentPart("conversation_history", text));
            dirty = true;
        }

        private void ProcessToolCallItem(JsonElement payload, string? entryTimestamp)
        {
            string id = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? string.Empty;
            string name = ReadString(payload, "name") ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

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

        private void ProcessToolResultItem(JsonElement payload, string? entryTimestamp)
        {
            string? outputText = ReadCodexToolResultText(payload);
            if (string.IsNullOrEmpty(outputText))
            {
                return;
            }

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

        private ReaderTurn? BuildSnapshotIfNonEmpty()
        {
            List<ContentPart> snapshotParts = [];
            int order = 0;
            if (systemPrompt is not null)
            {
                snapshotParts.Add(new ContentPart("system_prompt", systemPrompt, Order: order++));
            }

            if (instructionContext is not null)
            {
                snapshotParts.Add(new ContentPart("instruction_context", instructionContext, Order: order++));
            }

            foreach (ContentPart item in items)
            {
                snapshotParts.Add(item with { Order = order++ });
            }

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
                    lines.Add(ReadString(item, "text") ?? item.GetRawText());
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

    private static string? ReadCodexSystemPrompt(JsonElement payload) =>
        HarnessJson.ReadCodexSystemPrompt(payload);

    private static string? ReadCodexAgentsMd(JsonElement payload) =>
        HarnessJson.ReadCodexAgentsMd(payload);

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

    private static string? ReadCodexMessageText(JsonElement payload) =>
        HarnessJson.ReadCodexMessageText(payload);

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
        string model = detectedModel ?? ReadString(payload, "model") ?? fallbackModel;

        call = new ParsedProviderCall
        {
            Timestamp = timestamp ?? "1970-01-01T00:00:00.000Z",
            Provider = CodexProviderId,
            Model = model,
            InputTokens = fresh,
            OutputTokens = ReadLong(usageContainer, "output_tokens"),
            CacheReadInputTokens = cached,
            CachedInputTokens = cached,
            ReasoningTokens = ReadLong(usageContainer, "reasoning_output_tokens"),
            SessionId = sessionId,
            DeduplicationKey = string.Create(CultureInfo.InvariantCulture, $"codex:{sessionId}:turn-{turnIndex}"),
        };

        return true;
    }

    private static string? ReadString(JsonElement obj, string key) =>
        HarnessJson.ReadString(obj, key);

    private static long ReadLong(JsonElement obj, string key) =>
        TryReadLong(obj, key, out long val) ? val : 0L;

    private static bool TryReadLong(JsonElement obj, string key, out long value) =>
        HarnessJson.TryReadLong(obj, key, out value);

    private static bool TryReadInt(JsonElement obj, string key, out int value) =>
        HarnessJson.TryReadInt(obj, key, out value);
}
