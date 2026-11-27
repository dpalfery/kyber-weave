using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.CopilotVscode;

/// <summary>
/// Non-fatal parse problem encountered while replaying a Copilot VS Code <c>chatSessions</c> journal line.
/// </summary>
/// <param name="LineNumber">1-based line number within the journal file.</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record CopilotVscodeJournalProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Tool invocation metadata extracted from a VS Code Copilot <c>result.metadata.toolCallRounds</c> entry.
/// </summary>
/// <param name="CallId">Optional native tool call correlation identifier (<c>id</c>, <c>toolCallId</c>, or <c>callId</c>).</param>
/// <param name="ToolName">Invoked tool name (<c>toolName</c>, <c>name</c>, or <c>tool</c>).</param>
/// <param name="RawArgumentsText">Optional serialized tool input payload prior to measurement/withholding.</param>
public record CopilotVscodeToolCall(
    string? CallId,
    string ToolName,
    string? RawArgumentsText);

/// <summary>
/// Reconstructed VS Code Copilot chat-session request entry from <c>requests[]</c>.
/// </summary>
/// <param name="RequestIndex">Zero-based index of the request in the reconstructed <c>requests</c> array.</param>
/// <param name="RequestId">Optional native request identifier (<c>requestId</c>).</param>
/// <param name="Timestamp">Optional normalized ISO-8601 UTC request timestamp (<c>timestamp</c>).</param>
/// <param name="ModelId">Optional raw model identifier on the request (<c>modelId</c>).</param>
/// <param name="ResolvedModel">Optional resolved model identifier from <c>result.metadata.resolvedModel</c>.</param>
/// <param name="AgentId">Optional agent identifier from <c>result.metadata.agentId</c>.</param>
/// <param name="UserMessage">Optional extracted user prompt text from <c>message</c>.</param>
/// <param name="ResponseText">Optional extracted assistant reply text from <c>response</c> or <c>result.response</c>.</param>
/// <param name="ModeInstructions">Optional deduplicated mode instructions from <c>message.modeInstructions</c> and <c>modeInstructions</c>.</param>
/// <param name="PromptTokens">Optional input prompt token count from <c>result.metadata.promptTokens</c>.</param>
/// <param name="MetadataOutputTokens">Optional output token count from <c>result.metadata.outputTokens</c>.</param>
/// <param name="CompletionTokens">Optional fallback output token count from <c>completionTokens</c>.</param>
/// <param name="ToolCalls">Tool invocations extracted from <c>result.metadata.toolCallRounds</c>.</param>
public record CopilotVscodeRequest(
    int RequestIndex,
    string? RequestId,
    string? Timestamp,
    string? ModelId,
    string? ResolvedModel,
    string? AgentId,
    string? UserMessage,
    string? ResponseText,
    string? ModeInstructions,
    long? PromptTokens,
    long? MetadataOutputTokens,
    long? CompletionTokens,
    IReadOnlyList<CopilotVscodeToolCall> ToolCalls);

/// <summary>
/// Replayed VS Code Copilot <c>chatSessions</c> journal document.
/// </summary>
/// <param name="SessionId">Resolved native session identifier (<c>sessionId</c>), when present.</param>
/// <param name="CreationDate">Optional normalized ISO-8601 UTC session creation timestamp (<c>creationDate</c>).</param>
/// <param name="JournalVersion">Optional journal format version integer (<c>version</c>).</param>
/// <param name="Requests">Reconstructed ordered chat requests.</param>
/// <param name="ParseProblems">Non-fatal line-level JSONL parse diagnostics.</param>
public record CopilotVscodeJournalDocument(
    string? SessionId,
    string? CreationDate,
    long? JournalVersion,
    IReadOnlyList<CopilotVscodeRequest> Requests,
    IReadOnlyList<CopilotVscodeJournalProblem> ParseProblems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> translating VS Code <c>chatSessions</c> append/edit journals
/// (<c>kind: 0/1/2</c>) and JSON session snapshots into normalized <see cref="CollectedBatch"/> records.
/// </summary>
/// <remarks>
/// Mirrors the TypeScript <c>replayChatSessionJournal</c> + <c>copilotVscodeReader</c> semantics:
/// each request emits an <c>llm.invoke</c> record whose content parts carry the cumulative prior
/// requests and responses plus the current user message and mode instructions, while excluding the
/// current request's own response. Prototype-pollution path keys (<c>__proto__</c>, <c>prototype</c>,
/// <c>constructor</c>) and negative indices are rejected during journal replay.
/// </remarks>
public class CopilotVscodeMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for VS Code Copilot chat-session journals.</summary>
    public const string CopilotVscodeHarnessId = "copilot-vscode";

    /// <summary>Problem code emitted when a line in a <c>chatSessions</c> journal cannot be parsed as JSON.</summary>
    public const string MalformedLineCode = "COPILOT_VSCODE_MALFORMED_LINE";

    private const int MaxArrayPaddingElements = 65_536;

    private static readonly HashSet<string> ForbiddenPathKeys = new(StringComparer.Ordinal)
    {
        "__proto__",
        "prototype",
        "constructor",
    };

    /// <inheritdoc />
    public string HarnessId => CopilotVscodeHarnessId;

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        CollectedSource source = new(CopilotVscodeHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);
        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        CopilotVscodeJournalDocument document = Parse(unit.RawText, unit.Unit.SourceKey);
        string sessionId = ResolveSessionId(document.SessionId, unit.Unit.SourceKey);
        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId, importedAtUtc);

        if (document.Requests.Count == 0)
        {
            return new CollectedBatch(source, [], [], problems);
        }

        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{CopilotVscodeHarnessId}:{sessionId}");
        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        for (int index = 0; index < document.Requests.Count; index++)
        {
            AddRequestRecords(
                records,
                document,
                index,
                sessionId,
                traceId,
                unit.LastWriteUtc,
                importedAtUtc,
                seenRecordIds);
        }

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: sessionId,
                HarnessId: CopilotVscodeHarnessId,
                NativeSessionId: sessionId,
                Workspace: null,
                Title: null,
                ReportedUsage: []),
        ];

        return new CollectedBatch(source, sessions, records, problems);
    }

    private static void AddRequestRecords(
        List<CollectedRecord> records,
        CopilotVscodeJournalDocument document,
        int index,
        string sessionId,
        string traceId,
        DateTimeOffset sourceLastWriteUtc,
        DateTimeOffset importedAtUtc,
        HashSet<string> seenRecordIds)
    {
        CopilotVscodeRequest current = document.Requests[index];
        string model = ResolveModel(current);
        List<CollectedContentPart> parts = [];
        int position = 0;

        for (int prevIndex = 0; prevIndex < index; prevIndex++)
        {
            CopilotVscodeRequest previous = document.Requests[prevIndex];
            if (!string.IsNullOrWhiteSpace(previous.UserMessage))
            {
                parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ConversationHistory, previous.UserMessage, "user", model));
            }

            if (!string.IsNullOrWhiteSpace(previous.ResponseText))
            {
                parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ConversationHistory, previous.ResponseText, "assistant", model));
            }
        }

        string? correctionRule = null;
        if (!string.IsNullOrWhiteSpace(current.UserMessage))
        {
            CorrectionDetectionResult correction = CanonFindings.DetectUserCorrection(current.UserMessage);
            if (correction.Matched)
            {
                correctionRule = correction.Rule;
            }

            parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ConversationHistory, current.UserMessage, "user", model));
        }

        if (!string.IsNullOrWhiteSpace(current.ModeInstructions))
        {
            parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.InstructionContext, current.ModeInstructions, "system", model));
        }

        string recordId = BuildUniqueRecordId(sessionId, current, seenRecordIds);
        string recordName = !string.Equals(model, "unknown", StringComparison.Ordinal)
            ? $"copilot:{model}"
            : "copilot-vscode:request";
        DateTimeOffset startedAt = ResolveTimestamp(current.Timestamp, document.CreationDate, sourceLastWriteUtc, importedAtUtc);
        string? agentInstanceId = SafeOptionalText(current.AgentId);

        long? inputTokens = current.PromptTokens is >= 0 ? current.PromptTokens.Value : null;
        long? outputTokens = ResolveOutputTokens(current);

        List<CollectedTokenCount> tokens = [];
        if (inputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, inputTokens.Value));
        }

        if (outputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, outputTokens.Value));
        }

        List<string> skillsInvoked = ExtractInvokedSkills(current.ToolCalls);

        records.Add(new CollectedRecord(
            RecordId: recordId,
            HarnessId: CopilotVscodeHarnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: recordName,
            StartedAt: startedAt,
            Status: RecordStatus.Ok,
            SessionKey: sessionId,
            ParentRecordId: null,
            TraceId: traceId,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: tokens,
            ReportedCost: null,
            Parts: parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: skillsInvoked,
            AgentInstanceId: agentInstanceId,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(
                ErrorMessage: null,
                EditFailures: null,
                CorrectionRule: correctionRule,
                DeclaredContextWindow: null),
            ReportedInputTokens: inputTokens,
            ReportedOutputTokens: outputTokens));

        for (int toolIndex = 0; toolIndex < current.ToolCalls.Count; toolIndex++)
        {
            AddToolRecord(records, current.ToolCalls[toolIndex], toolIndex, recordId, sessionId, traceId, startedAt, agentInstanceId, model, seenRecordIds);
        }
    }

    private static void AddToolRecord(
        List<CollectedRecord> records,
        CopilotVscodeToolCall toolCall,
        int toolIndex,
        string recordId,
        string sessionId,
        string traceId,
        DateTimeOffset startedAt,
        string? agentInstanceId,
        string model,
        HashSet<string> seenRecordIds)
    {
        string toolRecordId = BuildUniqueToolRecordId(recordId, toolIndex, seenRecordIds);
        string callId = SafeOptionalText(toolCall.CallId)
            ?? string.Create(CultureInfo.InvariantCulture, $"tool-{toolIndex}");
        CollectedToolContent? toolInput = !string.IsNullOrEmpty(toolCall.RawArgumentsText)
            ? ToolContentMeasurer.Measure(toolCall.RawArgumentsText, model: model).ToToolContent(includeFingerprint: true)
            : null;

        records.Add(new CollectedRecord(
            RecordId: toolRecordId,
            HarnessId: CopilotVscodeHarnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: toolCall.ToolName,
            StartedAt: startedAt,
            Status: RecordStatus.Ok,
            SessionKey: sessionId,
            ParentRecordId: recordId,
            TraceId: traceId,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: new CollectedToolCall(
                CallId: callId,
                ToolName: toolCall.ToolName,
                McpServerName: null,
                McpToolName: null,
                Input: toolInput,
                Result: null,
                Succeeded: null,
                Error: null,
                ExitCode: null),
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: agentInstanceId,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null));
    }

    /// <summary>
    /// Replays a VS Code <c>chatSessions</c> JSONL journal (or parses a direct JSON snapshot) into a
    /// static <see cref="CopilotVscodeJournalDocument"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the chat session file.</param>
    /// <param name="sourceKey">Optional path locator used for session-id fallback and problem locations.</param>
    /// <returns>The parsed <see cref="CopilotVscodeJournalDocument"/>.</returns>
    public static CopilotVscodeJournalDocument Parse(string? rawText, string? sourceKey = null)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new CopilotVscodeJournalDocument(
                SessionId: ExtractSessionIdFromPath(sourceKey),
                CreationDate: null,
                JournalVersion: null,
                Requests: [],
                ParseProblems: []);
        }

        List<CopilotVscodeJournalProblem> problems = [];
        ReplayNode? root = ReplayChatSessionJournal(rawText, problems, out bool sawJournalEntry);

        if (!sawJournalEntry)
        {
            ReplayNode? snapshot = TryParseDirectSnapshot(rawText);
            if (snapshot is not null)
            {
                root = snapshot;
                problems.Clear();
            }
        }

        if (root is not { Kind: ReplayNodeKind.Object } || root.ObjectValues is null)
        {
            return new CopilotVscodeJournalDocument(
                SessionId: ExtractSessionIdFromPath(sourceKey),
                CreationDate: null,
                JournalVersion: null,
                Requests: [],
                ParseProblems: problems);
        }

        Dictionary<string, ReplayNode?> obj = root.ObjectValues;
        string? sessionId = ReadNodeText(obj.GetValueOrDefault("sessionId")) ?? ExtractSessionIdFromPath(sourceKey);
        string? creationDate = ParseTimestampIso(obj.GetValueOrDefault("creationDate"));
        long? journalVersion = ReadOptionalNonNegativeLong(obj.GetValueOrDefault("version"));

        List<CopilotVscodeRequest> requests = [];
        ReplayNode? requestsNode = obj.GetValueOrDefault("requests");
        if (requestsNode is { Kind: ReplayNodeKind.Array, ArrayValues: not null })
        {
            for (int index = 0; index < requestsNode.ArrayValues.Count; index++)
            {
                ReplayNode? reqNode = requestsNode.ArrayValues[index];
                if (reqNode is not { Kind: ReplayNodeKind.Object, ObjectValues: not null })
                {
                    continue;
                }

                requests.Add(BuildRequest(reqNode.ObjectValues, index));
            }
        }

        return new CopilotVscodeJournalDocument(
            SessionId: sessionId,
            CreationDate: creationDate,
            JournalVersion: journalVersion,
            Requests: requests,
            ParseProblems: problems);
    }

    private static ReplayNode? TryParseDirectSnapshot(string rawText)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool hasSnapshotFields = doc.RootElement.TryGetProperty("requests", out _)
                || doc.RootElement.TryGetProperty("sessionId", out _);
            if (!hasSnapshotFields)
            {
                return null;
            }

            return ReplayNode.FromJson(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReplayNode? ReplayChatSessionJournal(
        string rawText,
        List<CopilotVscodeJournalProblem> problems,
        out bool sawJournalEntry)
    {
        ReplayNode root = ReplayNode.CreateObject();
        sawJournalEntry = false;

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

            JsonDocument entryDoc;
            try
            {
                entryDoc = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                problems.Add(new CopilotVscodeJournalProblem(
                    LineNumber: lineNumber,
                    Code: MalformedLineCode,
                    Message: $"Skipping malformed JSONL line {lineNumber.ToString(CultureInfo.InvariantCulture)}: {ex.Message}"));
                continue;
            }

            using (entryDoc)
            {
                if (entryDoc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                JsonElement entry = entryDoc.RootElement;
                if (!entry.TryGetProperty("kind", out JsonElement kindElement)
                    || kindElement.ValueKind != JsonValueKind.Number
                    || !kindElement.TryGetInt32(out int kind))
                {
                    continue;
                }

                if (kind == 0)
                {
                    sawJournalEntry = true;
                    root = entry.TryGetProperty("v", out JsonElement valueEl)
                        ? ReplayNode.FromJson(valueEl)
                        : ReplayNode.Null();
                    continue;
                }

                if (kind == 1)
                {
                    sawJournalEntry = true;
                    List<PathSegment>? path = entry.TryGetProperty("k", out JsonElement pathEl)
                        ? ParseChatJournalPath(pathEl, useFallback: false)
                        : null;
                    if (path is null)
                    {
                        continue;
                    }

                    ReplayNode valueNode = entry.TryGetProperty("v", out JsonElement setVal)
                        ? ReplayNode.FromJson(setVal)
                        : ReplayNode.Null();
                    root = ApplyChatJournalSet(root, path, valueNode);
                    continue;
                }

                if (kind == 2)
                {
                    sawJournalEntry = true;
                    bool hasPath = entry.TryGetProperty("k", out JsonElement pathEl);
                    List<PathSegment>? path = ParseChatJournalPath(hasPath ? pathEl : default, useFallback: !hasPath);
                    if (path is null)
                    {
                        continue;
                    }

                    List<ReplayNode> items = [];
                    if (entry.TryGetProperty("v", out JsonElement appendVal) && appendVal.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in appendVal.EnumerateArray())
                        {
                            items.Add(ReplayNode.FromJson(item));
                        }
                    }

                    root = ApplyChatJournalAppend(root, path, items);
                }
            }
        }

        return root;
    }

    private static List<PathSegment>? ParseChatJournalPath(JsonElement pathElement, bool useFallback)
    {
        if (useFallback)
        {
            return [PathSegment.ForKey("requests")];
        }

        if (pathElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<PathSegment> segments = [];
        foreach (JsonElement item in pathElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
            {
                if (!item.TryGetInt32(out int index) || index < 0 || !IsWholeNumber(item))
                {
                    return null;
                }

                segments.Add(PathSegment.ForIndex(index));
                continue;
            }

            if (item.ValueKind == JsonValueKind.String)
            {
                string? key = item.GetString();
                if (key is null || ForbiddenPathKeys.Contains(key))
                {
                    return null;
                }

                segments.Add(PathSegment.ForKey(key));
                continue;
            }

            return null;
        }

        return segments;
    }

    private static bool IsWholeNumber(JsonElement element)
    {
        if (!element.TryGetDouble(out double d))
        {
            return false;
        }

        return double.IsFinite(d) && d == Math.Truncate(d);
    }

    private static ReplayNode ApplyChatJournalSet(ReplayNode root, List<PathSegment> path, ReplayNode value)
    {
        if (path.Count == 0)
        {
            return value;
        }

        ReplayNode workingRoot = root.IsContainer ? root : ReplayNode.CreateObject();
        ReplayNode? parent = EnsureReplayParent(workingRoot, path);
        if (parent is null)
        {
            return workingRoot;
        }

        SetReplayValue(parent, path[^1], value);
        return workingRoot;
    }

    private static ReplayNode ApplyChatJournalAppend(ReplayNode root, List<PathSegment> path, List<ReplayNode> items)
    {
        ReplayNode workingRoot = root.IsContainer ? root : ReplayNode.CreateObject();

        if (path.Count == 0)
        {
            if (workingRoot.Kind == ReplayNodeKind.Array && workingRoot.ArrayValues is not null)
            {
                foreach (ReplayNode item in items)
                {
                    workingRoot.ArrayValues.Add(item);
                }
            }

            return workingRoot;
        }

        ReplayNode? parent = EnsureReplayParent(workingRoot, path);
        if (parent is null)
        {
            return workingRoot;
        }

        PathSegment last = path[^1];
        ReplayNode? target = GetReplayValue(parent, last);
        if (target is not { Kind: ReplayNodeKind.Array, ArrayValues: not null })
        {
            target = ReplayNode.CreateArray();
            SetReplayValue(parent, last, target);
        }

        if (target.ArrayValues is { } arrayValues)
        {
            foreach (ReplayNode item in items)
            {
                arrayValues.Add(item);
            }
        }

        return workingRoot;
    }

    private static ReplayNode? EnsureReplayParent(ReplayNode root, List<PathSegment> path)
    {
        ReplayNode current = root;
        for (int i = 0; i < path.Count - 1; i++)
        {
            PathSegment segment = path[i];
            PathSegment nextSegment = path[i + 1];
            ReplayNode? child = GetReplayValue(current, segment);
            if (child is null || !child.IsContainer)
            {
                ReplayNode created = nextSegment.IsIndex ? ReplayNode.CreateArray() : ReplayNode.CreateObject();
                if (!SetReplayValue(current, segment, created))
                {
                    return null;
                }

                current = created;
                continue;
            }

            current = child;
        }

        return current;
    }

    private static ReplayNode? GetReplayValue(ReplayNode container, PathSegment segment)
    {
        if (container.Kind == ReplayNodeKind.Object && container.ObjectValues is not null)
        {
            return container.ObjectValues.GetValueOrDefault(segment.KeyName);
        }

        if (container.Kind == ReplayNodeKind.Array && container.ArrayValues is not null && segment.IsIndex)
        {
            return segment.Index < container.ArrayValues.Count ? container.ArrayValues[segment.Index] : null;
        }

        return null;
    }

    private static bool SetReplayValue(ReplayNode container, PathSegment segment, ReplayNode value)
    {
        if (container.Kind == ReplayNodeKind.Object && container.ObjectValues is not null)
        {
            container.ObjectValues[segment.KeyName] = value;
            return true;
        }

        if (container.Kind == ReplayNodeKind.Array && container.ArrayValues is not null && segment.IsIndex)
        {
            if (segment.Index - container.ArrayValues.Count > MaxArrayPaddingElements)
            {
                return false;
            }

            while (container.ArrayValues.Count <= segment.Index)
            {
                container.ArrayValues.Add(ReplayNode.Null());
            }

            container.ArrayValues[segment.Index] = value;
            return true;
        }

        return false;
    }

    private static CopilotVscodeRequest BuildRequest(Dictionary<string, ReplayNode?> req, int index)
    {
        string? requestId = ReadNodeText(req.GetValueOrDefault("requestId"));
        string? timestamp = ParseTimestampIso(req.GetValueOrDefault("timestamp"));
        string? modelId = ReadNodeText(req.GetValueOrDefault("modelId"));

        Dictionary<string, ReplayNode?>? resultObj = GetObject(req.GetValueOrDefault("result"));
        Dictionary<string, ReplayNode?>? metadataObj = resultObj is not null
            ? GetObject(resultObj.GetValueOrDefault("metadata"))
            : null;

        string? resolvedModel = metadataObj is not null ? ReadNodeText(metadataObj.GetValueOrDefault("resolvedModel")) : null;
        string? agentId = metadataObj is not null ? ReadNodeText(metadataObj.GetValueOrDefault("agentId")) : null;
        long? promptTokens = metadataObj is not null ? ReadOptionalNonNegativeLong(metadataObj.GetValueOrDefault("promptTokens")) : null;
        long? metadataOutputTokens = metadataObj is not null ? ReadOptionalNonNegativeLong(metadataObj.GetValueOrDefault("outputTokens")) : null;
        long? completionTokens = ReadOptionalNonNegativeLong(req.GetValueOrDefault("completionTokens"));

        string? userMessage = ExtractMessageText(req.GetValueOrDefault("message"));
        string? responseText = ExtractResponseText(req, resultObj);
        string? modeInstructions = ExtractModeInstructions(req);
        IReadOnlyList<CopilotVscodeToolCall> toolCalls = metadataObj is not null
            ? ExtractToolCalls(metadataObj.GetValueOrDefault("toolCallRounds"))
            : [];

        return new CopilotVscodeRequest(
            RequestIndex: index,
            RequestId: requestId,
            Timestamp: timestamp,
            ModelId: modelId,
            ResolvedModel: resolvedModel,
            AgentId: agentId,
            UserMessage: userMessage,
            ResponseText: responseText,
            ModeInstructions: modeInstructions,
            PromptTokens: promptTokens,
            MetadataOutputTokens: metadataOutputTokens,
            CompletionTokens: completionTokens,
            ToolCalls: toolCalls);
    }

    private static string? ExtractMessageText(ReplayNode? messageNode)
    {
        if (messageNode is null)
        {
            return null;
        }

        if (messageNode.Kind == ReplayNodeKind.String)
        {
            return ReadNonEmptyRawString(messageNode);
        }

        if (messageNode.Kind != ReplayNodeKind.Object || messageNode.ObjectValues is null)
        {
            return null;
        }

        return ReadNonEmptyRawString(messageNode.ObjectValues.GetValueOrDefault("text"))
            ?? ExtractTextFromParts(messageNode.ObjectValues.GetValueOrDefault("parts"));
    }

    private static string? ExtractResponseText(
        Dictionary<string, ReplayNode?> req,
        Dictionary<string, ReplayNode?>? resultObj)
    {
        ReplayNode? responseNode = req.GetValueOrDefault("response");
        Dictionary<string, ReplayNode?>? responseObj = GetObject(responseNode);
        if (responseObj is null && resultObj is not null)
        {
            responseObj = GetObject(resultObj.GetValueOrDefault("response"));
        }

        if (responseObj is null)
        {
            return ReadNonEmptyRawString(responseNode);
        }

        return ReadNonEmptyRawString(responseObj.GetValueOrDefault("text"))
            ?? ExtractTextFromParts(responseObj.GetValueOrDefault("parts"));
    }

    private static string? ExtractTextFromParts(ReplayNode? partsNode)
    {
        if (partsNode is not { Kind: ReplayNodeKind.Array, ArrayValues: not null })
        {
            return null;
        }

        List<string> texts = [];
        foreach (ReplayNode? part in partsNode.ArrayValues)
        {
            if (part is null)
            {
                continue;
            }

            if (part.Kind == ReplayNodeKind.String)
            {
                string? str = ReadNonEmptyRawString(part);
                if (str is not null)
                {
                    texts.Add(str);
                }

                continue;
            }

            if (part.Kind == ReplayNodeKind.Object && part.ObjectValues is not null)
            {
                string? text = ReadNonEmptyRawString(part.ObjectValues.GetValueOrDefault("text"));
                if (text is not null)
                {
                    texts.Add(text);
                }
            }
        }

        return texts.Count > 0 ? string.Join('\n', texts) : null;
    }

    private static string? ExtractModeInstructions(Dictionary<string, ReplayNode?> req)
    {
        Dictionary<string, ReplayNode?>? messageObj = GetObject(req.GetValueOrDefault("message"));
        List<string> unique = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        string? fromMessage = messageObj is not null
            ? ReadNonEmptyRawString(messageObj.GetValueOrDefault("modeInstructions"))
            : null;
        if (fromMessage is not null && seen.Add(fromMessage))
        {
            unique.Add(fromMessage);
        }

        string? fromRequest = ReadNonEmptyRawString(req.GetValueOrDefault("modeInstructions"));
        if (fromRequest is not null && seen.Add(fromRequest))
        {
            unique.Add(fromRequest);
        }

        return unique.Count > 0 ? string.Join('\n', unique) : null;
    }

    private static IReadOnlyList<CopilotVscodeToolCall> ExtractToolCalls(ReplayNode? roundsNode)
    {
        if (roundsNode is not { Kind: ReplayNodeKind.Array, ArrayValues: not null })
        {
            return [];
        }

        List<CopilotVscodeToolCall> calls = [];
        foreach (ReplayNode? round in roundsNode.ArrayValues)
        {
            Dictionary<string, ReplayNode?>? roundObj = GetObject(round);
            if (roundObj is null)
            {
                continue;
            }

            TryAddToolCallFromObject(roundObj, calls);

            foreach (string key in new[] { "tools", "toolCalls", "toolRequests" })
            {
                ReplayNode? entriesNode = roundObj.GetValueOrDefault(key);
                if (entriesNode is not { Kind: ReplayNodeKind.Array, ArrayValues: not null })
                {
                    continue;
                }

                foreach (ReplayNode? entry in entriesNode.ArrayValues)
                {
                    if (entry is { Kind: ReplayNodeKind.String })
                    {
                        string? name = ReadNodeText(entry);
                        if (name is not null)
                        {
                            calls.Add(new CopilotVscodeToolCall(null, name, null));
                        }

                        continue;
                    }

                    Dictionary<string, ReplayNode?>? entryObj = GetObject(entry);
                    if (entryObj is not null)
                    {
                        TryAddToolCallFromObject(entryObj, calls);
                    }
                }
            }
        }

        return calls;
    }

    private static void TryAddToolCallFromObject(
        Dictionary<string, ReplayNode?> obj,
        List<CopilotVscodeToolCall> target)
    {
        string? name = ReadNodeText(obj.GetValueOrDefault("toolName"))
            ?? ReadNodeText(obj.GetValueOrDefault("name"))
            ?? ReadNodeText(obj.GetValueOrDefault("tool"));
        if (name is null)
        {
            return;
        }

        string? callId = ReadNodeText(obj.GetValueOrDefault("id"))
            ?? ReadNodeText(obj.GetValueOrDefault("toolCallId"))
            ?? ReadNodeText(obj.GetValueOrDefault("callId"));

        ReplayNode? argsNode = obj.GetValueOrDefault("arguments") ?? obj.GetValueOrDefault("input");
        string? rawArgs = SerializeNodeAsRawInput(argsNode);
        target.Add(new CopilotVscodeToolCall(callId, name, rawArgs));
    }

    private static string? SerializeNodeAsRawInput(ReplayNode? node)
    {
        if (node is null || node.Kind == ReplayNodeKind.Null)
        {
            return null;
        }

        if (node.Kind == ReplayNodeKind.String)
        {
            return node.StringValue;
        }

        return node.ToJsonString();
    }

    private static List<string> ExtractInvokedSkills(IReadOnlyList<CopilotVscodeToolCall> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return [];
        }

        List<string> skills = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CopilotVscodeToolCall call in toolCalls)
        {
            if (!string.Equals(call.ToolName, "skill", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(call.RawArgumentsText))
            {
                continue;
            }

            try
            {
                using JsonDocument argsDoc = JsonDocument.Parse(call.RawArgumentsText);
                if (argsDoc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? skillName = ReadJsonString(argsDoc.RootElement, "skill")
                    ?? ReadJsonString(argsDoc.RootElement, "name");
                string? safeSkill = SafeOptionalText(skillName);
                if (safeSkill is not null && seen.Add(safeSkill))
                {
                    skills.Add(safeSkill);
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON argument strings.
            }
        }

        return skills;
    }

    private static string? ReadJsonString(JsonElement obj, string propertyName) =>
        HarnessJson.ReadStringOnly(obj, propertyName);

    private static string ResolveModel(CopilotVscodeRequest request)
    {
        string? resolved = SafeOptionalText(request.ResolvedModel);
        if (resolved is not null)
        {
            return resolved;
        }

        string? rawModelId = SafeOptionalText(request.ModelId);
        if (rawModelId is not null)
        {
            const string copilotPrefix = "copilot/";
            string stripped = rawModelId.StartsWith(copilotPrefix, StringComparison.Ordinal)
                ? rawModelId[copilotPrefix.Length..].Trim()
                : rawModelId;
            string? safeStripped = SafeOptionalText(stripped);
            if (safeStripped is not null)
            {
                return safeStripped;
            }
        }

        return "unknown";
    }

    private static long? ResolveOutputTokens(CopilotVscodeRequest request)
    {
        if (request.MetadataOutputTokens is > 0)
        {
            return request.MetadataOutputTokens.Value;
        }

        if (request.CompletionTokens is > 0)
        {
            return request.CompletionTokens.Value;
        }

        if (request.MetadataOutputTokens == 0 || request.CompletionTokens == 0)
        {
            return 0L;
        }

        return null;
    }

    private static string BuildUniqueRecordId(
        string sessionId,
        CopilotVscodeRequest request,
        HashSet<string> seenRecordIds)
    {
        string anchor = SafeOptionalText(request.RequestId)
            ?? string.Create(CultureInfo.InvariantCulture, $"request-{request.RequestIndex}");
        string candidate = string.Create(
            CultureInfo.InvariantCulture,
            $"{CanonSynth.SynthSpanPrefix}{CopilotVscodeHarnessId}:{sessionId}:{anchor}");

        if (seenRecordIds.Add(candidate))
        {
            return candidate;
        }

        string disambiguated = string.Create(CultureInfo.InvariantCulture, $"{candidate}:idx-{request.RequestIndex}");
        _ = seenRecordIds.Add(disambiguated);
        return disambiguated;
    }

    private static string BuildUniqueToolRecordId(
        string parentRecordId,
        int toolIndex,
        HashSet<string> seenRecordIds)
    {
        string candidate = string.Create(CultureInfo.InvariantCulture, $"{parentRecordId}:tool:{toolIndex}");
        int suffix = toolIndex;
        while (!seenRecordIds.Add(candidate))
        {
            suffix++;
            candidate = string.Create(CultureInfo.InvariantCulture, $"{parentRecordId}:tool:{suffix}");
        }

        return candidate;
    }

    private static string ResolveSessionId(string? parsedSessionId, string sourceKey)
    {
        string? explicitId = SafeOptionalText(parsedSessionId);
        if (explicitId is not null)
        {
            return explicitId;
        }

        string? fromPath = SafeOptionalText(ExtractSessionIdFromPath(sourceKey));
        if (fromPath is not null)
        {
            return fromPath;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return $"copilot-vscode-{Convert.ToHexStringLower(digest)[..16]}";
    }

    private static string? ExtractSessionIdFromPath(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(sourceKey);
        return string.IsNullOrWhiteSpace(stem) ? null : stem.Trim();
    }

    private static DateTimeOffset ResolveTimestamp(
        string? requestTimestamp,
        string? creationDate,
        DateTimeOffset sourceLastWriteUtc,
        DateTimeOffset importedAtUtc)
    {
        if (TryParseTimestamp(requestTimestamp, out DateTimeOffset parsedRequest))
        {
            return parsedRequest;
        }

        if (TryParseTimestamp(creationDate, out DateTimeOffset parsedCreation))
        {
            return parsedCreation;
        }

        if (sourceLastWriteUtc != default)
        {
            return sourceLastWriteUtc.ToUniversalTime();
        }

        return importedAtUtc.ToUniversalTime();
    }

    private static bool TryParseTimestamp(string? raw, out DateTimeOffset timestampUtc) =>
        HarnessJson.TryParseTimestamp(raw, out timestampUtc);

    private static string? ParseTimestampIso(ReplayNode? node)
    {
        if (node is null || node.Kind == ReplayNodeKind.Null)
        {
            return null;
        }

        if (node.Kind == ReplayNodeKind.Number && node.NumberValue.HasValue)
        {
            return EpochToIso(node.NumberValue.Value);
        }

        if (node.Kind == ReplayNodeKind.String && !string.IsNullOrWhiteSpace(node.StringValue))
        {
            string trimmed = node.StringValue.Trim();
            if (IsNumericString(trimmed)
                && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric))
            {
                return EpochToIso(numeric);
            }

            if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed))
            {
                return parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static bool IsNumericString(string value)
    {
        bool seenDot = false;
        if (value.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsAsciiDigit(c))
            {
                continue;
            }

            if (c == '.' && !seenDot && i > 0 && i < value.Length - 1)
            {
                seenDot = true;
                continue;
            }

            return false;
        }

        return true;
    }

    private static string EpochToIso(double epoch)
    {
        if (!double.IsFinite(epoch) || epoch <= 0)
        {
            return DateTimeOffset.UnixEpoch.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        double ms = epoch > 1e15
            ? Math.Floor(epoch / 1_000_000.0)
            : epoch > 1e12
                ? epoch
                : epoch * 1_000.0;

        long wholeMs = (long)Math.Floor(ms);
        return DateTimeOffset.FromUnixTimeMilliseconds(wholeMs)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<CopilotVscodeJournalProblem> parseProblems,
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

        foreach (CopilotVscodeJournalProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"copilot-vscode-malformed:{sourceKey}:line-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed Copilot VS Code journal line.";

            problems.Add(new CollectedProblem(
                ProblemKey: problemKey,
                Severity: "warning",
                Code: parseProblem.Code,
                Message: message,
                SpanId: null,
                Location: location,
                SessionKey: sessionId,
                HarnessId: CopilotVscodeHarnessId,
                TimestampUtc: timestampUtc,
                Stack: null));
        }

        return problems;
    }

    private static Dictionary<string, ReplayNode?>? GetObject(ReplayNode? node) =>
        node is { Kind: ReplayNodeKind.Object } ? node.ObjectValues : null;

    private static string? ReadNonEmptyRawString(ReplayNode? node)
    {
        if (node is not { Kind: ReplayNodeKind.String } || string.IsNullOrWhiteSpace(node.StringValue))
        {
            return null;
        }

        return node.StringValue;
    }

    private static string? ReadNodeText(ReplayNode? node)
    {
        if (node is not { Kind: ReplayNodeKind.String })
        {
            return null;
        }

        return SafeOptionalText(node.StringValue);
    }

    private static long? ReadOptionalNonNegativeLong(ReplayNode? node)
    {
        if (node is not { Kind: ReplayNodeKind.Number } || !node.NumberValue.HasValue)
        {
            return null;
        }

        double d = node.NumberValue.Value;
        if (!double.IsFinite(d) || d < 0 || d > long.MaxValue || d != Math.Truncate(d))
        {
            return null;
        }

        return (long)d;
    }

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);

    private readonly struct PathSegment
    {
        private PathSegment(bool isIndex, int index, string keyName)
        {
            IsIndex = isIndex;
            Index = index;
            KeyName = keyName;
        }

        public bool IsIndex { get; }

        public int Index { get; }

        public string KeyName { get; }

        public static PathSegment ForIndex(int index) =>
            new(true, index, index.ToString(CultureInfo.InvariantCulture));

        public static PathSegment ForKey(string key) =>
            new(false, -1, key);
    }

    private enum ReplayNodeKind
    {
        Null,
        Object,
        Array,
        String,
        Number,
        Boolean,
    }

    private class ReplayNode
    {
        private ReplayNode(
            ReplayNodeKind kind,
            Dictionary<string, ReplayNode?>? objectValues = null,
            List<ReplayNode?>? arrayValues = null,
            string? stringValue = null,
            double? numberValue = null,
            bool boolValue = false)
        {
            Kind = kind;
            ObjectValues = objectValues;
            ArrayValues = arrayValues;
            StringValue = stringValue;
            NumberValue = numberValue;
            BoolValue = boolValue;
        }

        public ReplayNodeKind Kind { get; }

        public Dictionary<string, ReplayNode?>? ObjectValues { get; }

        public List<ReplayNode?>? ArrayValues { get; }

        public string? StringValue { get; }

        public double? NumberValue { get; }

        public bool BoolValue { get; }

        public bool IsContainer => Kind is ReplayNodeKind.Object or ReplayNodeKind.Array;

        public static ReplayNode Null() => new(ReplayNodeKind.Null);

        public static ReplayNode CreateObject() =>
            new(ReplayNodeKind.Object, objectValues: new Dictionary<string, ReplayNode?>(StringComparer.Ordinal));

        public static ReplayNode CreateArray() =>
            new(ReplayNodeKind.Array, arrayValues: []);

        public static ReplayNode FromJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, ReplayNode?> obj = new(StringComparer.Ordinal);
                    foreach (JsonProperty prop in element.EnumerateObject())
                    {
                        if (ForbiddenPathKeys.Contains(prop.Name))
                        {
                            continue;
                        }

                        obj[prop.Name] = FromJson(prop.Value);
                    }

                    return new ReplayNode(ReplayNodeKind.Object, objectValues: obj);

                case JsonValueKind.Array:
                    List<ReplayNode?> arr = [];
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        arr.Add(FromJson(item));
                    }

                    return new ReplayNode(ReplayNodeKind.Array, arrayValues: arr);

                case JsonValueKind.String:
                    return new ReplayNode(ReplayNodeKind.String, stringValue: element.GetString());

                case JsonValueKind.Number:
                    return element.TryGetDouble(out double d)
                        ? new ReplayNode(ReplayNodeKind.Number, numberValue: d)
                        : Null();

                case JsonValueKind.True:
                    return new ReplayNode(ReplayNodeKind.Boolean, boolValue: true);

                case JsonValueKind.False:
                    return new ReplayNode(ReplayNodeKind.Boolean, boolValue: false);

                default:
                    return Null();
            }
        }

        public string ToJsonString()
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void WriteTo(Utf8JsonWriter writer)
        {
            switch (Kind)
            {
                case ReplayNodeKind.Object when ObjectValues is not null:
                    writer.WriteStartObject();
                    foreach (KeyValuePair<string, ReplayNode?> kv in ObjectValues)
                    {
                        writer.WritePropertyName(kv.Key);
                        if (kv.Value is null)
                        {
                            writer.WriteNullValue();
                        }
                        else
                        {
                            kv.Value.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                    break;

                case ReplayNodeKind.Array when ArrayValues is not null:
                    writer.WriteStartArray();
                    foreach (ReplayNode? item in ArrayValues)
                    {
                        if (item is null)
                        {
                            writer.WriteNullValue();
                        }
                        else
                        {
                            item.WriteTo(writer);
                        }
                    }

                    writer.WriteEndArray();
                    break;

                case ReplayNodeKind.String:
                    writer.WriteStringValue(StringValue);
                    break;

                case ReplayNodeKind.Number when NumberValue.HasValue:
                    writer.WriteNumberValue(NumberValue.Value);
                    break;

                case ReplayNodeKind.Boolean:
                    writer.WriteBooleanValue(BoolValue);
                    break;

                default:
                    writer.WriteNullValue();
                    break;
            }
        }
    }
}
