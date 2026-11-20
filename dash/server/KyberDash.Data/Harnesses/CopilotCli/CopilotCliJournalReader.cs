using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;

namespace KyberDash.Data.Harnesses.CopilotCli;

/// <summary>
/// Discovers, reads, and parses Copilot CLI session journals (<c>~/.copilot/session-state/&lt;id&gt;/events.jsonl</c>)
/// into static native DTOs while skipping <c>pending-session:*</c> draft directories and sessions.
/// </summary>
public class CopilotCliJournalReader : IHarnessSourceReader
{
    /// <summary>Canonical harness identifier for Copilot CLI journals.</summary>
    public const string CopilotCliHarnessId = "copilot-cli";

    /// <summary>Upstream provider identifier reported on discovered units.</summary>
    public const string CopilotProviderId = "copilot";

    /// <summary>Parser identifier stamped onto discovered source units.</summary>
    public const string CopilotCliParserId = "copilot-cli-reader";

    /// <summary>Source format identifier for Copilot CLI JSONL event journals.</summary>
    public const string CopilotCliFormat = "copilot-cli-jsonl";

    /// <summary>Relative root directory under the user's home directory holding session-state folders.</summary>
    public const string SessionStateRelativeRoot = ".copilot/session-state";

    /// <summary>Journal file name inside each session directory.</summary>
    public const string EventsFileName = "events.jsonl";

    /// <summary>Prefix identifying draft Copilot CLI sessions that must be skipped.</summary>
    public const string PendingSessionPrefix = "pending-session:";

    /// <summary>Problem code emitted when a line in <c>events.jsonl</c> cannot be parsed as a JSON object.</summary>
    public const string MalformedLineCode = "COPILOT_CLI_MALFORMED_LINE";

    /// <inheritdoc />
    public string HarnessId => CopilotCliHarnessId;

    /// <inheritdoc />
    public bool IsOtlpOnly => false;

    /// <inheritdoc />
    public SourceDiscoveryResult Discover(SourceDiscoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.FileSystem);

        string sessionStateRoot = Path.Combine(
            context.HomeDirectory,
            SessionStateRelativeRoot.Replace('/', Path.DirectorySeparatorChar));

        if (!context.FileSystem.DirectoryExists(sessionStateRoot))
        {
            return new SourceDiscoveryResult([], []);
        }

        List<SourceUnit> units = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string filePath in context.FileSystem
            .EnumerateFiles(sessionStateRoot, EventsFileName, recursive: true)
            .OrderBy(static path => path, StringComparer.Ordinal))
        {
            string fullPath = context.FileSystem.GetFullPath(filePath);
            if (IsPendingSessionPath(fullPath) || !seen.Add(fullPath))
            {
                continue;
            }

            if (!context.FileSystem.TryGetFileStats(fullPath, out long lengthBytes, out DateTimeOffset lastWriteUtc))
            {
                continue;
            }

            if (context.SinceUtc.HasValue && lastWriteUtc < context.SinceUtc.Value)
            {
                continue;
            }

            string sessionDirName = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? string.Empty);
            string revision = string.Create(CultureInfo.InvariantCulture, $"{lengthBytes}-{lastWriteUtc.UtcTicks}");

            units.Add(new SourceUnit(
                HarnessId: CopilotCliHarnessId,
                SourceKey: fullPath,
                ProviderId: CopilotProviderId,
                ParserId: CopilotCliParserId,
                ParserContractVersion: HarnessSourceRegistry.DefaultParserContractVersion,
                Format: CopilotCliFormat,
                SourceRootLabel: sessionDirName,
                RevisionToken: revision,
                LastWriteUtc: lastWriteUtc,
                SizeBytes: lengthBytes,
                CompanionKey: null,
                Kind: SourceKind.File));
        }

        return new SourceDiscoveryResult(units, []);
    }

    /// <inheritdoc />
    public NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (IsPendingSessionPath(unit.SourceKey) || !fileSystem.FileExists(unit.SourceKey))
        {
            return null;
        }

        bool hasStats = fileSystem.TryGetFileStats(unit.SourceKey, out long lengthBytes, out DateTimeOffset lastWriteUtc);
        string rawText = fileSystem.ReadAllText(unit.SourceKey);
        if (!hasStats || lengthBytes == 0)
        {
            lengthBytes = Encoding.UTF8.GetByteCount(rawText);
        }

        DateTimeOffset effectiveWriteUtc = hasStats ? lastWriteUtc : (unit.LastWriteUtc ?? default);
        return new NativeSourceUnit(
            Unit: unit,
            RawText: rawText,
            SizeBytes: lengthBytes,
            LastWriteUtc: effectiveWriteUtc);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="sessionId"/> identifies a draft <c>pending-session:*</c> session.
    /// </summary>
    /// <param name="sessionId">Candidate session identifier.</param>
    /// <returns><see langword="true"/> if <paramref name="sessionId"/> starts with <c>pending-session:</c>.</returns>
    public static bool IsPendingSessionId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId)
        && sessionId.Trim().StartsWith(PendingSessionPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when any path segment in <paramref name="sourcePath"/> starts with <c>pending-session:</c>.
    /// </summary>
    /// <param name="sourcePath">Filesystem path or locator to inspect.</param>
    /// <returns><see langword="true"/> when the path belongs to a <c>pending-session:*</c> folder.</returns>
    public static bool IsPendingSessionPath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        string[] segments = sourcePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment.StartsWith(PendingSessionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses a Copilot CLI <c>events.jsonl</c> payload into a static <see cref="CopilotCliJournalDocument"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of <c>events.jsonl</c>.</param>
    /// <param name="sourceKey">Optional path locator used for session-id fallback and diagnostics.</param>
    /// <returns>The parsed <see cref="CopilotCliJournalDocument"/>.</returns>
    public static CopilotCliJournalDocument Parse(string? rawText, string? sourceKey = null)
    {
        bool pathIsPending = IsPendingSessionPath(sourceKey);
        string? fallbackSessionId = ExtractSessionIdFromPath(sourceKey);

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new CopilotCliJournalDocument(
                SessionId: fallbackSessionId,
                IsPendingSession: pathIsPending || IsPendingSessionId(fallbackSessionId),
                InitialModel: null,
                CopilotVersion: null,
                Title: null,
                InitialContext: null,
                Events: [],
                ParseProblems: []);
        }

        List<CopilotCliJournalEvent> events = [];
        List<CopilotCliJournalProblem> problems = [];

        string? discoveredSessionId = null;
        string? initialModel = null;
        string? copilotVersion = null;
        string? initialTitle = null;
        CopilotCliContextInfo? initialContext = null;

        using StringReader reader = new(rawText);
        string? line;
        int lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                problems.Add(new CopilotCliJournalProblem(
                    LineNumber: lineNumber,
                    Code: MalformedLineCode,
                    Message: $"Skipping malformed JSONL line {lineNumber.ToString(CultureInfo.InvariantCulture)}: {ex.Message}"));
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new CopilotCliJournalProblem(
                        LineNumber: lineNumber,
                        Code: MalformedLineCode,
                        Message: $"Skipping non-object JSONL line {lineNumber.ToString(CultureInfo.InvariantCulture)}."));
                    continue;
                }

                CopilotCliJournalEvent? parsedEvent = ParseEventObject(document.RootElement, lineNumber);
                if (parsedEvent is null)
                {
                    problems.Add(new CopilotCliJournalProblem(
                        LineNumber: lineNumber,
                        Code: MalformedLineCode,
                        Message: $"Skipping Copilot CLI event without a type at line {lineNumber.ToString(CultureInfo.InvariantCulture)}."));
                    continue;
                }

                events.Add(parsedEvent);

                if (discoveredSessionId is null && !string.IsNullOrWhiteSpace(parsedEvent.SessionId))
                {
                    discoveredSessionId = parsedEvent.SessionId;
                }

                if (string.Equals(parsedEvent.EventType, "session.start", StringComparison.Ordinal))
                {
                    initialModel ??= parsedEvent.Model;
                    initialTitle ??= parsedEvent.Title;
                    initialContext ??= parsedEvent.Context;
                    copilotVersion ??= ReadSessionStartVersion(document.RootElement);
                }
            }
        }

        string? resolvedSessionId = discoveredSessionId ?? fallbackSessionId;
        bool isPending = pathIsPending || IsPendingSessionId(resolvedSessionId);

        return new CopilotCliJournalDocument(
            SessionId: resolvedSessionId,
            IsPendingSession: isPending,
            InitialModel: initialModel,
            CopilotVersion: copilotVersion,
            Title: initialTitle,
            InitialContext: initialContext,
            Events: events,
            ParseProblems: problems);
    }

    private static CopilotCliJournalEvent? ParseEventObject(JsonElement root, int lineNumber)
    {
        string? eventType = ReadString(root, "type");
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return null;
        }

        JsonElement data = root.TryGetProperty("data", out JsonElement dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement
            : root;

        string? eventId = ReadString(root, "id");
        string? parentId = ReadString(root, "parentId");
        string? timestamp = ReadString(root, "timestamp") ?? ReadString(data, "startTime") ?? ReadString(data, "timestamp");
        string? agentId = ReadString(root, "agentId") ?? ReadString(data, "agentId");
        string? sessionId = ReadString(data, "sessionId") ?? ReadString(root, "sessionId");
        string? messageId = ReadString(data, "messageId") ?? ReadString(data, "id");
        string? content = ReadString(data, "content") ?? ReadString(data, "transformedContent") ?? ReadString(data, "text");
        string? reasoningText = ReadString(data, "reasoningText");
        string? model = ReadString(data, "newModel") ?? ReadString(data, "model") ?? ReadString(data, "selectedModel");
        string? previousModel = ReadString(data, "previousModel");

        long? inputTokens = ReadOptionalNonNegativeLong(data, "inputTokens");
        long? outputTokens = ReadOptionalNonNegativeLong(data, "outputTokens");
        long? cacheReadTokens = ReadOptionalNonNegativeLong(data, "cacheReadTokens");
        long? cacheWriteTokens = ReadOptionalNonNegativeLong(data, "cacheWriteTokens");
        long? reasoningTokens = ReadOptionalNonNegativeLong(data, "reasoningTokens");
        long? durationMs = ReadOptionalNonNegativeLong(data, "durationMs");
        long? declaredContextWindow = ReadOptionalPositiveLong(data, "declaredContextWindow")
            ?? ReadOptionalPositiveLong(data, "contextWindow")
            ?? ReadOptionalPositiveLong(data, "maxContextTokens");
        string? title = ReadString(data, "title") ?? ReadString(data, "summary");
        CopilotCliContextInfo? context = ParseContextInfo(data);
        IReadOnlyList<CopilotCliToolRequest> toolRequests = ParseToolRequests(data);
        IReadOnlyList<CopilotCliModelUsage> modelMetrics = ParseModelMetrics(data);

        return new CopilotCliJournalEvent(
            LineNumber: lineNumber,
            EventType: eventType.Trim(),
            EventId: eventId,
            ParentId: parentId,
            Timestamp: timestamp,
            AgentId: agentId,
            SessionId: sessionId,
            MessageId: messageId,
            Content: content,
            ReasoningText: reasoningText,
            Model: model,
            PreviousModel: previousModel,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CacheReadTokens: cacheReadTokens,
            CacheWriteTokens: cacheWriteTokens,
            ReasoningTokens: reasoningTokens,
            DurationMs: durationMs,
            DeclaredContextWindow: declaredContextWindow,
            Title: title,
            Context: context,
            ToolRequests: toolRequests,
            ModelMetrics: modelMetrics);
    }

    private static string? ReadSessionStartVersion(JsonElement root)
    {
        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
        {
            return ReadString(data, "copilotVersion");
        }

        return ReadString(root, "copilotVersion");
    }

    private static CopilotCliContextInfo? ParseContextInfo(JsonElement data)
    {
        JsonElement contextObj = data.TryGetProperty("context", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : data;

        string? workingDirectory = ReadString(contextObj, "cwd") ?? ReadString(contextObj, "workingDirectory");
        string? repository = ReadString(contextObj, "repository");
        string? branch = ReadString(contextObj, "branch");
        string? gitRoot = ReadString(contextObj, "gitRoot");

        if (workingDirectory is null && repository is null && branch is null && gitRoot is null)
        {
            return null;
        }

        return new CopilotCliContextInfo(workingDirectory, repository, branch, gitRoot);
    }

    private static IReadOnlyList<CopilotCliModelUsage> ParseModelMetrics(JsonElement data)
    {
        if (!data.TryGetProperty("modelMetrics", out JsonElement metricsEl)
            && !data.TryGetProperty("model_metrics", out metricsEl))
        {
            return [];
        }

        if (metricsEl.ValueKind == JsonValueKind.Array)
        {
            List<CopilotCliModelUsage> metrics = [];
            foreach (JsonElement item in metricsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                metrics.Add(ParseSingleModelUsage(item, defaultModel: null));
            }

            return metrics;
        }

        if (metricsEl.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (HasModelUsageFields(metricsEl))
        {
            return [ParseSingleModelUsage(metricsEl, defaultModel: null)];
        }

        List<CopilotCliModelUsage> mapped = [];
        foreach (JsonProperty property in metricsEl.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            mapped.Add(ParseSingleModelUsage(property.Value, property.Name));
        }

        return mapped;
    }

    private static bool HasModelUsageFields(JsonElement element)
    {
        return element.TryGetProperty("model", out _)
            || element.TryGetProperty("modelId", out _)
            || element.TryGetProperty("model_id", out _)
            || element.TryGetProperty("inputTokens", out _)
            || element.TryGetProperty("outputTokens", out _)
            || element.TryGetProperty("cacheReadTokens", out _)
            || element.TryGetProperty("cacheWriteTokens", out _)
            || element.TryGetProperty("cacheCreationTokens", out _)
            || element.TryGetProperty("reasoningTokens", out _);
    }

    private static CopilotCliModelUsage ParseSingleModelUsage(JsonElement element, string? defaultModel)
    {
        string? model = ReadString(element, "model")
            ?? ReadString(element, "modelId")
            ?? ReadString(element, "model_id");
        if (model is null && !string.IsNullOrWhiteSpace(defaultModel))
        {
            model = defaultModel.Trim();
        }

        return new CopilotCliModelUsage(
            model,
            ReadOptionalNonNegativeLong(element, "inputTokens"),
            ReadOptionalNonNegativeLong(element, "outputTokens"),
            ReadOptionalNonNegativeLong(element, "cacheReadTokens"),
            ReadOptionalNonNegativeLong(element, "cacheWriteTokens")
                ?? ReadOptionalNonNegativeLong(element, "cacheCreationTokens"),
            ReadOptionalNonNegativeLong(element, "reasoningTokens"));
    }

    private static IReadOnlyList<CopilotCliToolRequest> ParseToolRequests(JsonElement data)
    {
        if (!data.TryGetProperty("toolRequests", out JsonElement requestsEl) || requestsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CopilotCliToolRequest> requests = [];
        foreach (JsonElement item in requestsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? toolCallId = ReadString(item, "toolCallId") ?? ReadString(item, "id");
            string? name = ReadString(item, "name");
            string? skillName = string.Equals(name, "skill", StringComparison.OrdinalIgnoreCase)
                ? ReadSkillName(item)
                : null;

            requests.Add(new CopilotCliToolRequest(toolCallId, name, skillName));
        }

        return requests;
    }

    private static string? ReadSkillName(JsonElement toolRequest)
    {
        if (!toolRequest.TryGetProperty("arguments", out JsonElement args))
        {
            return null;
        }

        if (args.ValueKind == JsonValueKind.Object)
        {
            return ReadString(args, "skill") ?? ReadString(args, "name");
        }

        if (args.ValueKind == JsonValueKind.String)
        {
            string? raw = args.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using JsonDocument parsedArgs = JsonDocument.Parse(raw);
                if (parsedArgs.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return ReadString(parsedArgs.RootElement, "skill")
                        ?? ReadString(parsedArgs.RootElement, "name");
                }
            }
            catch (JsonException)
            {
                // Non-JSON string arguments are not structured skill requests.
            }
        }

        return null;
    }

    private static string? ExtractSessionIdFromPath(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return null;
        }

        string fileName = Path.GetFileName(sourceKey);
        if (!string.Equals(fileName, EventsFileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(sourceKey);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string parentName = Path.GetFileName(directory);
        if (string.IsNullOrWhiteSpace(parentName)
            || string.Equals(parentName, "session-state", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parentName, ".copilot", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parentName;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        HarnessJson.ReadString(element, propertyName);

    private static long? ReadOptionalNonNegativeLong(JsonElement element, string propertyName) =>
        HarnessJson.ReadOptionalNonNegativeLong(element, propertyName);

    private static long? ReadOptionalPositiveLong(JsonElement element, string propertyName)
    {
        long? value = ReadOptionalNonNegativeLong(element, propertyName);
        return value is > 0 ? value : null;
    }
}
