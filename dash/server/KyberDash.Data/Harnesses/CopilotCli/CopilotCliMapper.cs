using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.CopilotCli;

/// <summary>
/// Workspace context metadata extracted from Copilot CLI <c>session.start</c> or context-change journal events.
/// </summary>
/// <param name="WorkingDirectory">Active working directory (<c>cwd</c>).</param>
/// <param name="Repository">Repository slug or name (<c>repository</c> or derived from <c>gitRoot</c>).</param>
/// <param name="Branch">Active git branch (<c>branch</c>).</param>
/// <param name="GitRoot">Git repository root directory (<c>gitRoot</c>).</param>
public record CopilotCliContextInfo(
    string? WorkingDirectory,
    string? Repository,
    string? Branch,
    string? GitRoot);

/// <summary>
/// Tool request summary extracted from an <c>assistant.message</c> event's <c>toolRequests</c> array.
/// </summary>
/// <param name="ToolCallId">Native tool call correlation identifier (<c>toolCallId</c>).</param>
/// <param name="Name">Requested tool name (<c>name</c>).</param>
/// <param name="SkillName">Extracted skill identifier when <see cref="Name"/> is <c>skill</c>.</param>
public record CopilotCliToolRequest(
    string? ToolCallId,
    string? Name,
    string? SkillName);

/// <summary>
/// Per-model token usage snapshot from a Copilot CLI <c>session.shutdown</c> event's <c>modelMetrics</c>.
/// </summary>
/// <param name="Model">Model identifier for this usage snapshot.</param>
/// <param name="InputTokens">Input token count as the CLI reports it, cache-inclusive (cache read and write are part of it) when reported.</param>
/// <param name="OutputTokens">Generated output token count when reported.</param>
/// <param name="CacheReadTokens">Prompt-cache read token count when reported.</param>
/// <param name="CacheWriteTokens">Prompt-cache creation/write token count when reported.</param>
/// <param name="ReasoningTokens">Reasoning token count when reported.</param>
public record CopilotCliModelUsage(
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    long? ReasoningTokens);

/// <summary>
/// Non-fatal parse problem encountered while reading a Copilot CLI <c>events.jsonl</c> line.
/// </summary>
/// <param name="LineNumber">1-based line number within the journal file.</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record CopilotCliJournalProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Static typed representation of a single JSONL event line from a Copilot CLI <c>events.jsonl</c> journal.
/// </summary>
/// <param name="LineNumber">1-based line number within <c>events.jsonl</c>.</param>
/// <param name="EventType">Native Copilot CLI event type (for example <c>session.start</c>, <c>assistant.message</c>).</param>
/// <param name="EventId">Optional native event identifier (<c>id</c>).</param>
/// <param name="ParentId">Optional parent event identifier (<c>parentId</c>).</param>
/// <param name="Timestamp">Optional ISO-8601 UTC timestamp string (<c>timestamp</c> or <c>data.startTime</c>).</param>
/// <param name="AgentId">Optional subagent identifier; <see langword="null"/> for root agent events.</param>
/// <param name="SessionId">Optional native session identifier carried on the event.</param>
/// <param name="MessageId">Optional assistant message identifier (<c>data.messageId</c>).</param>
/// <param name="Content">Optional message or prompt text (<c>data.content</c> / <c>data.text</c>).</param>
/// <param name="ReasoningText">Optional reasoning text (<c>data.reasoningText</c> or <c>assistant.reasoning</c> content).</param>
/// <param name="Model">Optional model identifier (<c>data.model</c>, <c>data.newModel</c>, or <c>data.selectedModel</c>).</param>
/// <param name="PreviousModel">Optional prior model identifier on <c>session.model_change</c> events.</param>
/// <param name="InputTokens">Optional input token count when reported on the event.</param>
/// <param name="OutputTokens">Optional output token count; absent on Copilot CLI 1.0.81+ messages.</param>
/// <param name="CacheReadTokens">Optional cache-read token count when reported on the event.</param>
/// <param name="CacheWriteTokens">Optional cache-write token count when reported on the event.</param>
/// <param name="ReasoningTokens">Optional reasoning token count when reported on the event.</param>
/// <param name="DurationMs">Optional reported duration in milliseconds.</param>
/// <param name="DeclaredContextWindow">Optional explicit context window limit declared on the event.</param>
/// <param name="Title">Optional session title carried on the event.</param>
/// <param name="Context">Optional workspace context snapshot carried on the event.</param>
/// <param name="ToolRequests">Tool requests attached to an <c>assistant.message</c> event.</param>
/// <param name="ModelMetrics">Per-model token snapshots from a <c>session.shutdown</c> event's <c>modelMetrics</c>.</param>
public record CopilotCliJournalEvent(
    int LineNumber,
    string EventType,
    string? EventId,
    string? ParentId,
    string? Timestamp,
    string? AgentId,
    string? SessionId,
    string? MessageId,
    string? Content,
    string? ReasoningText,
    string? Model,
    string? PreviousModel,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    long? ReasoningTokens,
    long? DurationMs,
    long? DeclaredContextWindow,
    string? Title,
    CopilotCliContextInfo? Context,
    IReadOnlyList<CopilotCliToolRequest> ToolRequests,
    IReadOnlyList<CopilotCliModelUsage> ModelMetrics);

/// <summary>
/// Parsed Copilot CLI session journal produced by <see cref="CopilotCliJournalReader"/>.
/// </summary>
/// <param name="SessionId">Resolved native session identifier, or <see langword="null"/> when unknown.</param>
/// <param name="IsPendingSession">True when the journal belongs to a <c>pending-session:*</c> draft that must be skipped.</param>
/// <param name="InitialModel">Initial model from <c>session.start</c>.</param>
/// <param name="CopilotVersion">Copilot CLI version from <c>session.start</c>.</param>
/// <param name="Title">Optional session title.</param>
/// <param name="InitialContext">Initial workspace context from <c>session.start</c>.</param>
/// <param name="Events">Ordered parsed journal events.</param>
/// <param name="ParseProblems">Non-fatal line-level parse problems encountered while reading.</param>
public record CopilotCliJournalDocument(
    string? SessionId,
    bool IsPendingSession,
    string? InitialModel,
    string? CopilotVersion,
    string? Title,
    CopilotCliContextInfo? InitialContext,
    IReadOnlyList<CopilotCliJournalEvent> Events,
    IReadOnlyList<CopilotCliJournalProblem> ParseProblems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> translating Copilot CLI <c>events.jsonl</c> journals into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// Every <c>assistant.message</c> emits an <c>llm.invoke</c> <see cref="CollectedRecord"/> regardless of
/// whether <c>outputTokens</c> is present (Copilot CLI 1.0.81+ omits <c>outputTokens</c>); when
/// <c>outputTokens</c> is absent, no <see cref="TokenClass.Output"/> row is emitted.
/// Draft <c>pending-session:*</c> journals are skipped entirely.
/// </remarks>
public class CopilotCliMapper : IHarnessMapper
{
    private const string RootAgentKey = "";

    /// <inheritdoc />
    public string HarnessId => CopilotCliJournalReader.CopilotCliHarnessId;

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        CollectedSource source = new(HarnessId, SourceKind.File, unit.Unit.SourceKey);

        if (CopilotCliJournalReader.IsPendingSessionPath(unit.Unit.SourceKey)
            || string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        CopilotCliJournalDocument document = CopilotCliJournalReader.Parse(unit.RawText, unit.Unit.SourceKey);
        if (document.IsPendingSession)
        {
            return new CollectedBatch(source, [], [], []);
        }

        string sessionId = ResolveSessionId(document.SessionId, unit.Unit.SourceKey);
        if (CopilotCliJournalReader.IsPendingSessionId(sessionId))
        {
            return new CollectedBatch(source, [], [], []);
        }

        CopilotCliMappingState state = new(unit, importedAtUtc, sessionId, document);
        CopilotCliToolTelemetry toolTelemetry = ParseToolTelemetry(unit.RawText);
        BuildMessageEntries(state, toolTelemetry);

        AttachSkillInvocations(
            toolTelemetry,
            state.MessageEntries,
            state.MessageByEventId,
            state.MessageByMessageId,
            state.MessageByToolCallId);

        List<CollectedRecord> records = BuildCorrelatedRecords(
            sessionId,
            HarnessId,
            toolTelemetry,
            state.MessageEntries,
            state.MessageByEventId,
            state.MessageByMessageId,
            state.SeenRecordIds);

        records.AddRange(BuildSubagentRecords(
            sessionId,
            HarnessId,
            toolTelemetry,
            records,
            state.SeenRecordIds));

        List<CollectedSession> sessions = BuildSessions(state, records);
        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId, importedAtUtc);
        return new CollectedBatch(source, sessions, records, problems);
    }

    private static List<CollectedSession> BuildSessions(CopilotCliMappingState state, List<CollectedRecord> records)
    {
        if (records.Count == 0 && state.Document.Events.Count == 0)
        {
            return [];
        }

        CollectedWorkspace? workspace = state.WorkingDirectory is not null
            || state.Repository is not null
            || state.Branch is not null
            ? new CollectedWorkspace(state.WorkingDirectory, state.Repository, state.Branch)
            : null;

        return
        [
            new CollectedSession(
                SessionKey: state.SessionId,
                HarnessId: CopilotCliJournalReader.CopilotCliHarnessId,
                NativeSessionId: state.SessionId,
                Workspace: workspace,
                Title: state.SessionTitle,
                ReportedUsage: BuildReportedUsage(state.ShutdownSnapshots, state.SessionModel)),
        ];
    }

    private static void BuildMessageEntries(CopilotCliMappingState state, CopilotCliToolTelemetry toolTelemetry)
    {
        foreach (CopilotCliJournalEvent evt in state.Document.Events)
        {
            string agentKey = string.IsNullOrWhiteSpace(evt.AgentId) ? RootAgentKey : evt.AgentId.Trim();

            if (evt.DeclaredContextWindow is > 0)
            {
                state.SessionDeclaredContextWindow = evt.DeclaredContextWindow.Value;
            }

            if (ApplyNonAssistantEvent(evt, state, agentKey))
            {
                continue;
            }

            if (!string.Equals(evt.EventType, "assistant.message", StringComparison.Ordinal))
            {
                continue;
            }

            AddAssistantMessage(evt, state, agentKey, toolTelemetry);
        }
    }

    private static bool ApplyNonAssistantEvent(
        CopilotCliJournalEvent evt,
        CopilotCliMappingState state,
        string agentKey)
    {
        if (IsSessionStart(evt.EventType))
        {
            string? startModel = SafeOptionalText(evt.Model);
            if (startModel is not null)
            {
                state.SessionModel = startModel;
            }

            string? startTitle = SafeOptionalText(evt.Title);
            if (startTitle is not null)
            {
                state.SessionTitle = startTitle;
            }

            ApplyContextUpdate(evt.Context, state);
            return true;
        }

        if (IsModelChange(evt.EventType))
        {
            string? newModel = SafeOptionalText(evt.Model);
            if (newModel is not null)
            {
                if (agentKey.Length == 0)
                {
                    state.SessionModel = newModel;
                }
                else
                {
                    state.AgentModels[agentKey] = newModel;
                }
            }

            return true;
        }

        if (IsContextChange(evt.EventType))
        {
            ApplyContextUpdate(evt.Context, state);
            string? updatedTitle = SafeOptionalText(evt.Title);
            if (updatedTitle is not null)
            {
                state.SessionTitle = updatedTitle;
            }

            return true;
        }

        if (IsShutdown(evt.EventType))
        {
            if (evt.ModelMetrics.Count > 0)
            {
                state.ShutdownSnapshots.Add(evt.ModelMetrics);
            }

            return true;
        }

        if (string.Equals(evt.EventType, "system.message", StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(evt.Content))
            {
                state.LatestSystemPrompts[agentKey] = evt.Content;
            }

            return true;
        }

        if (string.Equals(evt.EventType, "user.message", StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(evt.Content))
            {
                AddPendingMessage(state.PendingUserMessages, agentKey, evt.Content);
            }

            return true;
        }

        if (string.Equals(evt.EventType, "assistant.reasoning", StringComparison.Ordinal))
        {
            string? reasoning = !string.IsNullOrEmpty(evt.ReasoningText) ? evt.ReasoningText : evt.Content;
            if (!string.IsNullOrEmpty(reasoning))
            {
                AddPendingMessage(state.PendingReasonings, agentKey, reasoning);
            }

            return true;
        }

        return false;
    }

    private static void AddAssistantMessage(
        CopilotCliJournalEvent evt,
        CopilotCliMappingState state,
        string agentKey,
        CopilotCliToolTelemetry toolTelemetry)
    {
        string? activeModel = SafeOptionalText(evt.Model)
            ?? (agentKey.Length > 0 && state.AgentModels.TryGetValue(agentKey, out string? agentModel) ? agentModel : null)
            ?? state.SessionModel;

        List<CollectedContentPart> parts = [];
        int position = 0;
        string? correctionRule = null;

        if (state.LatestSystemPrompts.TryGetValue(agentKey, out string? systemPrompt))
        {
            parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.SystemPrompt, systemPrompt, "system", activeModel));
        }

        if (state.PendingUserMessages.TryGetValue(agentKey, out List<string>? userMessages))
        {
            foreach (string userMessage in userMessages)
            {
                if (correctionRule is null)
                {
                    CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(userMessage);
                    if (check.Matched)
                    {
                        correctionRule = check.Rule;
                    }
                }

                parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.UserMessage, userMessage, "user", activeModel));
            }

            userMessages.Clear();
        }

        if (state.PendingReasonings.TryGetValue(agentKey, out List<string>? reasonings))
        {
            foreach (string reasoning in reasonings)
            {
                parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.Reasoning, reasoning, "assistant", activeModel));
            }

            reasonings.Clear();
        }

        if (!string.IsNullOrEmpty(evt.ReasoningText))
        {
            parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.Reasoning, evt.ReasoningText, "assistant", activeModel));
        }

        if (!string.IsNullOrEmpty(evt.Content))
        {
            parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ConversationHistory, evt.Content, "assistant", activeModel));
        }

        List<CollectedTokenCount> tokens = BuildTokens(evt);
        List<string> skillsInvoked = ExtractInvokedSkills(evt.ToolRequests);
        DateTimeOffset startedAt = ParseTimestamp(evt.Timestamp, state.Unit.LastWriteUtc, state.ImportedAtUtc);
        long? durationMs = evt.DurationMs is >= 0 ? evt.DurationMs.Value : null;
        DateTimeOffset? endedAt = durationMs is > 0 ? startedAt.AddMilliseconds(durationMs.Value) : null;

        string recordId = BuildUniqueRecordId(state.SessionId, evt, state.SeenRecordIds);
        string recordName = activeModel is not null ? $"copilot:{activeModel}" : "copilot:assistant.message";
        string? agentInstanceId = agentKey.Length > 0 ? SafeOptionalText(agentKey) : null;
        long? recordContextWindow = evt.DeclaredContextWindow is > 0
            ? evt.DeclaredContextWindow.Value
            : state.SessionDeclaredContextWindow;

        CollectedRecord llmRecord = new(
            RecordId: recordId,
            HarnessId: CopilotCliJournalReader.CopilotCliHarnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: recordName,
            StartedAt: startedAt,
            Status: RecordStatus.Ok,
            SessionKey: state.SessionId,
            ParentRecordId: null,
            TraceId: $"{CanonSynth.SynthSpanPrefix}{CopilotCliJournalReader.CopilotCliHarnessId}:{state.SessionId}",
            Model: activeModel,
            EndedAt: endedAt,
            ReportedDurationMs: durationMs,
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
                DeclaredContextWindow: recordContextWindow),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);

        IReadOnlyList<ParsedToolRequest> requestsForLine = toolTelemetry.ToolRequestsByLine.TryGetValue(evt.LineNumber, out List<ParsedToolRequest>? parsedRequests)
            ? parsedRequests
            : [];

        AssistantMessageEntry entry = new(
            LineNumber: evt.LineNumber,
            EventId: SafeOptionalText(evt.EventId),
            MessageId: SafeOptionalText(evt.MessageId),
            AgentKey: agentKey,
            Record: llmRecord,
            ToolRequests: requestsForLine,
            SkillsInvoked: [.. skillsInvoked],
            SeenSkills: new HashSet<string>(skillsInvoked, StringComparer.Ordinal));

        state.MessageEntries.Add(entry);
        if (entry.EventId is not null)
        {
            state.MessageByEventId.TryAdd(entry.EventId, entry);
        }

        if (entry.MessageId is not null)
        {
            state.MessageByMessageId.TryAdd(entry.MessageId, entry);
        }

        foreach (ParsedToolRequest request in requestsForLine)
        {
            if (!string.IsNullOrEmpty(request.ToolCallId))
            {
                state.MessageByToolCallId.TryAdd(request.ToolCallId, entry);
            }
        }
    }

    private static bool IsSessionStart(string eventType) =>
        string.Equals(eventType, "session.start", StringComparison.Ordinal);

    private static bool IsModelChange(string eventType) =>
        eventType is "session.model_change" or "session.model_changed" or "model_change" or "model.change";

    private static bool IsShutdown(string eventType) =>
        eventType is "session.shutdown" or "session_shutdown" or "shutdown";

    private static IReadOnlyList<CollectedModelUsage> BuildReportedUsage(
        IReadOnlyList<IReadOnlyList<CopilotCliModelUsage>> shutdownSnapshots,
        string? sessionModel)
    {
        if (shutdownSnapshots.Count == 0)
        {
            return [];
        }

        List<CollectedModelUsage> usage = [];
        for (int position = 0; position < shutdownSnapshots.Count; position++)
        {
            foreach (CopilotCliModelUsage metric in shutdownSnapshots[position])
            {
                string? model = SafeOptionalText(metric.Model) ?? sessionModel;
                if (model is null)
                {
                    continue;
                }

                AddModelUsageRow(usage, position, model, TokenClass.Input, FreshInputTokens(metric));
                AddModelUsageRow(usage, position, model, TokenClass.CacheRead, metric.CacheReadTokens);
                AddModelUsageRow(usage, position, model, TokenClass.CacheWrite, metric.CacheWriteTokens);
                AddModelUsageRow(usage, position, model, TokenClass.Output, metric.OutputTokens);
                AddModelUsageRow(usage, position, model, TokenClass.Reasoning, metric.ReasoningTokens);
            }
        }

        return usage;
    }

    /// <summary>
    /// The shutdown <c>inputTokens</c> is cache-inclusive (input + cache read + cache write), so the
    /// fresh input stored is that count less both cache classes. The legacy provider did the same
    /// (copilot.ts:959-986 at fc80ddf9^). A negative cache class, or a result below zero, means the
    /// snapshot does not hold together: the fresh input is not measured and no input row is stored.
    /// The cache classes are compared with what is left before each is taken away, so no reported
    /// value, however large, can wrap the subtraction round to a positive count.
    /// </summary>
    private static long? FreshInputTokens(CopilotCliModelUsage metric)
    {
        if (metric.InputTokens is not { } input)
        {
            return null;
        }

        long cacheRead = metric.CacheReadTokens ?? 0;
        long cacheWrite = metric.CacheWriteTokens ?? 0;
        if (input < 0 || cacheRead < 0 || cacheWrite < 0)
        {
            return null;
        }

        if (cacheRead > input || cacheWrite > input - cacheRead)
        {
            return null;
        }

        return input - cacheRead - cacheWrite;
    }

    private static void AddModelUsageRow(
        List<CollectedModelUsage> usage,
        int snapshotPosition,
        string model,
        TokenClass tokenClass,
        long? count)
    {
        if (count is >= 0)
        {
            usage.Add(new CollectedModelUsage(snapshotPosition, model, tokenClass, count.Value));
        }
    }

    private static bool IsContextChange(string eventType) =>
        eventType is "session.context_changed"
            or "session.context_change"
            or "context.changed"
            or "context.change"
            or "session.resume"
            or "session.info";

    private static void ApplyContextUpdate(CopilotCliContextInfo? context, CopilotCliMappingState state)
    {
        if (context is null)
        {
            return;
        }

        string? nextWorkingDir = SafeOptionalText(context.WorkingDirectory);
        if (nextWorkingDir is not null)
        {
            state.WorkingDirectory = nextWorkingDir;
        }

        string? nextRepo = ResolveRepositoryName(context);
        if (nextRepo is not null)
        {
            state.Repository = nextRepo;
        }

        string? nextBranch = SafeOptionalText(context.Branch);
        if (nextBranch is not null)
        {
            state.Branch = nextBranch;
        }
    }

    private static string? ResolveRepositoryName(CopilotCliContextInfo? context)
    {
        if (context is null)
        {
            return null;
        }

        string? explicitRepo = SafeOptionalText(context.Repository);
        if (explicitRepo is not null)
        {
            return explicitRepo;
        }

        string? gitRoot = SafeOptionalText(context.GitRoot);
        if (gitRoot is null)
        {
            return null;
        }

        string trimmed = gitRoot.TrimEnd('/', '\\');
        string folder = Path.GetFileName(trimmed);
        return SafeOptionalText(folder);
    }

    private static void AddPendingMessage(
        Dictionary<string, List<string>> map,
        string agentKey,
        string text)
    {
        if (!map.TryGetValue(agentKey, out List<string>? list))
        {
            list = [];
            map[agentKey] = list;
        }

        list.Add(text);
    }

    private static List<CollectedTokenCount> BuildTokens(CopilotCliJournalEvent evt)
    {
        List<CollectedTokenCount> tokens = [];

        if (evt.InputTokens is >= 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, evt.InputTokens.Value));
        }

        if (evt.CacheReadTokens is >= 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, evt.CacheReadTokens.Value));
        }

        if (evt.CacheWriteTokens is >= 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, evt.CacheWriteTokens.Value));
        }

        // Absent outputTokens (Copilot CLI 1.0.81+) must leave zero TokenClass.Output rows rather than
        // synthesizing a zero token row.
        if (evt.OutputTokens is >= 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, evt.OutputTokens.Value));
        }

        if (evt.ReasoningTokens is >= 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Reasoning, evt.ReasoningTokens.Value));
        }

        return tokens;
    }

    private static List<string> ExtractInvokedSkills(IReadOnlyList<CopilotCliToolRequest> toolRequests)
    {
        if (toolRequests.Count == 0)
        {
            return [];
        }

        List<string> skills = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CopilotCliToolRequest request in toolRequests)
        {
            if (!string.Equals(request.Name, "skill", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? skillName = SafeOptionalText(request.SkillName);
            if (skillName is not null && seen.Add(skillName))
            {
                skills.Add(skillName);
            }
        }

        return skills;
    }

    private static string BuildUniqueRecordId(
        string sessionId,
        CopilotCliJournalEvent evt,
        HashSet<string> seenRecordIds)
    {
        string? nativeRecord = SafeOptionalText(evt.MessageId) ?? SafeOptionalText(evt.EventId);
        string baseAnchor = nativeRecord ?? string.Create(CultureInfo.InvariantCulture, $"line-{evt.LineNumber}");
        string candidate = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{CopilotCliJournalReader.CopilotCliHarnessId}:{sessionId}:{baseAnchor}");

        if (seenRecordIds.Add(candidate))
        {
            return candidate;
        }

        string disambiguated = string.Create(CultureInfo.InvariantCulture, $"{candidate}:line-{evt.LineNumber}");
        _ = seenRecordIds.Add(disambiguated);
        return disambiguated;
    }

    private static string ResolveSessionId(string? parsedSessionId, string sourceKey)
    {
        string? explicitId = SafeOptionalText(parsedSessionId);
        if (explicitId is not null)
        {
            return explicitId;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return $"copilot-cli-{Convert.ToHexStringLower(digest)[..16]}";
    }

    private static DateTimeOffset ParseTimestamp(
        string? rawTimestamp,
        DateTimeOffset sourceLastWriteUtc,
        DateTimeOffset importedAtUtc)
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

        if (sourceLastWriteUtc != default)
        {
            return sourceLastWriteUtc.ToUniversalTime();
        }

        return importedAtUtc.ToUniversalTime();
    }

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<CopilotCliJournalProblem> parseProblems,
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

        foreach (CopilotCliJournalProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"copilot-cli-malformed:{sourceKey}:line-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed Copilot CLI journal line.";

            problems.Add(new CollectedProblem(
                ProblemKey: problemKey,
                Severity: "warning",
                Code: parseProblem.Code,
                Message: message,
                SpanId: null,
                Location: location,
                SessionKey: sessionId,
                HarnessId: CopilotCliJournalReader.CopilotCliHarnessId,
                TimestampUtc: timestampUtc,
                Stack: null));
        }

        return problems;
    }

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);

    private static string SafeToolErrorMessage(string? rawMessage)
    {
        string? safe = SafeOptionalText(rawMessage);
        return safe ?? "Tool execution failed.";
    }

    private static CopilotCliToolTelemetry ParseToolTelemetry(string rawText)
    {
        Dictionary<int, List<ParsedToolRequest>> toolRequestsByLine = [];
        List<ParsedToolStart> toolStarts = [];
        Dictionary<string, ParsedToolStart> startByCallId = new(StringComparer.Ordinal);
        Dictionary<string, string> startEventIdToCallId = new(StringComparer.Ordinal);
        List<ParsedToolComplete> toolCompletes = [];
        Dictionary<string, ParsedToolComplete> completeByCallId = new(StringComparer.Ordinal);
        List<ParsedSkillInvoked> skillInvocations = [];
        List<ParsedSubagentEvent> subagentEvents = [];

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
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                JsonElement root = document.RootElement;
                string? eventType = ReadJsonString(root, "type");
                if (string.IsNullOrWhiteSpace(eventType))
                {
                    continue;
                }

                JsonElement data = root.TryGetProperty("data", out JsonElement dataEl) && dataEl.ValueKind == JsonValueKind.Object
                    ? dataEl
                    : root;

                string? rawAgentId = ReadJsonString(root, "agentId") ?? ReadJsonString(data, "agentId");
                string agentKey = string.IsNullOrWhiteSpace(rawAgentId) ? RootAgentKey : rawAgentId.Trim();
                string normalizedType = eventType.Trim();

                if (string.Equals(normalizedType, "assistant.message", StringComparison.Ordinal))
                {
                    AddLineToolRequests(toolRequestsByLine, lineNumber, data);
                    continue;
                }

                if (string.Equals(normalizedType, "tool.execution_start", StringComparison.Ordinal))
                {
                    AddToolStart(toolStarts, startByCallId, startEventIdToCallId, lineNumber, agentKey, root, data);
                    continue;
                }

                if (string.Equals(normalizedType, "tool.execution_complete", StringComparison.Ordinal))
                {
                    AddToolComplete(toolCompletes, completeByCallId, lineNumber, agentKey, root, data);
                    continue;
                }

                if (string.Equals(normalizedType, "skill.invoked", StringComparison.Ordinal))
                {
                    AddSkillInvocation(skillInvocations, lineNumber, agentKey, root, data);
                }

                // Sub-agent runs bracket delegation tool calls. `subagent.selected` is the legacy
                // sticky label with no spawning call, so it is dropped; started/completed/failed
                // carry the documented delegation join key (`data.toolCallId`).
                if (IsSubagentLifecycleEvent(normalizedType))
                {
                    AddSubagentEvent(subagentEvents, lineNumber, normalizedType, root, data);
                }
            }
        }

        return new CopilotCliToolTelemetry(
            ToolRequestsByLine: toolRequestsByLine,
            ToolStarts: toolStarts,
            StartByCallId: startByCallId,
            StartEventIdToCallId: startEventIdToCallId,
            ToolCompletes: toolCompletes,
            CompleteByCallId: completeByCallId,
            SkillInvocations: skillInvocations,
            SubagentEvents: subagentEvents);
    }

    private static void AddLineToolRequests(
        Dictionary<int, List<ParsedToolRequest>> toolRequestsByLine,
        int lineNumber,
        JsonElement data)
    {
        List<ParsedToolRequest> requests = ParseLineToolRequests(data);
        if (requests.Count > 0)
        {
            toolRequestsByLine[lineNumber] = requests;
        }
    }

    private static void AddToolStart(
        List<ParsedToolStart> toolStarts,
        Dictionary<string, ParsedToolStart> startByCallId,
        Dictionary<string, string> startEventIdToCallId,
        int lineNumber,
        string agentKey,
        JsonElement root,
        JsonElement data)
    {
        string? eventId = SafeOptionalText(ReadJsonString(root, "id"));
        string? parentId = SafeOptionalText(ReadJsonString(root, "parentId") ?? ReadJsonString(data, "parentId"));
        string? messageId = SafeOptionalText(ReadJsonString(data, "messageId"));
        string? toolCallId = SafeOptionalText(
            ReadJsonString(data, "toolCallId")
            ?? ReadJsonString(data, "callId")
            ?? ReadJsonString(data, "id")
            ?? ReadJsonString(root, "toolCallId"));
        string? toolName = SafeOptionalText(ReadJsonString(data, "toolName") ?? ReadJsonString(data, "name"));
        string? mcpServerName = SafeOptionalText(ReadMcpServerName(data));
        string? mcpToolName = SafeOptionalText(ReadMcpToolName(data));
        string? timestamp = ReadJsonString(root, "timestamp")
            ?? ReadJsonString(data, "startTime")
            ?? ReadJsonString(data, "timestamp");
        ToolContentMeasurement? inputMeasurement = MeasureToolInput(data);

        ParsedToolStart start = new(
            LineNumber: lineNumber,
            EventId: eventId,
            ParentId: parentId,
            MessageId: messageId,
            AgentKey: agentKey,
            ToolCallId: toolCallId,
            ToolName: toolName,
            McpServerName: mcpServerName,
            McpToolName: mcpToolName,
            Timestamp: timestamp,
            InputMeasurement: inputMeasurement);

        toolStarts.Add(start);
        if (toolCallId is not null)
        {
            _ = startByCallId.TryAdd(toolCallId, start);
            if (eventId is not null)
            {
                _ = startEventIdToCallId.TryAdd(eventId, toolCallId);
            }
        }
    }

    private static void AddToolComplete(
        List<ParsedToolComplete> toolCompletes,
        Dictionary<string, ParsedToolComplete> completeByCallId,
        int lineNumber,
        string agentKey,
        JsonElement root,
        JsonElement data)
    {
        string? eventId = SafeOptionalText(ReadJsonString(root, "id"));
        string? parentId = SafeOptionalText(ReadJsonString(root, "parentId") ?? ReadJsonString(data, "parentId"));
        string? messageId = SafeOptionalText(ReadJsonString(data, "messageId"));
        string? toolCallId = SafeOptionalText(
            ReadJsonString(data, "toolCallId")
            ?? ReadJsonString(data, "callId")
            ?? ReadJsonString(data, "id")
            ?? ReadJsonString(root, "toolCallId"));
        string? toolName = SafeOptionalText(ReadJsonString(data, "toolName") ?? ReadJsonString(data, "name"));
        string? mcpServerName = SafeOptionalText(ReadMcpServerName(data));
        string? mcpToolName = SafeOptionalText(ReadMcpToolName(data));
        string? timestamp = ReadJsonString(root, "timestamp")
            ?? ReadJsonString(data, "endTime")
            ?? ReadJsonString(data, "timestamp");
        long? durationMs = ReadOptionalNonNegativeLong(data, "durationMs")
            ?? ReadOptionalNonNegativeLong(data, "duration_ms");
        long? exitCode = ReadOptionalLong(data, "exitCode")
            ?? ReadOptionalLong(data, "exit_code")
            ?? ReadOptionalLongFromNested(data, "result", "exitCode");
        ToolContentMeasurement? resultMeasurement = MeasureToolResult(data);
        (bool succeeded, string? errorMessage, string? errorCode) = ParseCompletionOutcome(data, resultMeasurement);

        ParsedToolComplete complete = new(
            LineNumber: lineNumber,
            EventId: eventId,
            ParentId: parentId,
            MessageId: messageId,
            AgentKey: agentKey,
            ToolCallId: toolCallId,
            ToolName: toolName,
            McpServerName: mcpServerName,
            McpToolName: mcpToolName,
            Timestamp: timestamp,
            DurationMs: durationMs,
            Succeeded: succeeded,
            ResultMeasurement: resultMeasurement,
            ErrorMessage: errorMessage,
            ErrorCode: errorCode,
            ExitCode: exitCode);

        toolCompletes.Add(complete);
        if (toolCallId is not null)
        {
            _ = completeByCallId.TryAdd(toolCallId, complete);
        }
    }

    private static void AddSkillInvocation(
        List<ParsedSkillInvoked> skillInvocations,
        int lineNumber,
        string agentKey,
        JsonElement root,
        JsonElement data)
    {
        string? skillName = SafeOptionalText(
            ReadJsonString(data, "skillName")
            ?? ReadJsonString(data, "skill")
            ?? ReadJsonString(data, "name")
            ?? ReadJsonString(root, "skillName")
            ?? ReadJsonString(root, "skill")
            ?? ReadJsonString(root, "name"));

        if (skillName is null)
        {
            return;
        }

        string? toolCallId = SafeOptionalText(ReadJsonString(data, "toolCallId") ?? ReadJsonString(data, "callId"));
        string? messageId = SafeOptionalText(ReadJsonString(data, "messageId"));
        string? parentId = SafeOptionalText(ReadJsonString(root, "parentId") ?? ReadJsonString(data, "parentId"));

        skillInvocations.Add(new ParsedSkillInvoked(
            LineNumber: lineNumber,
            SkillName: skillName,
            ToolCallId: toolCallId,
            MessageId: messageId,
            ParentId: parentId,
            AgentKey: agentKey));
    }

    private static void AddSubagentEvent(
        List<ParsedSubagentEvent> subagentEvents,
        int lineNumber,
        string normalizedType,
        JsonElement root,
        JsonElement data)
    {
        ParsedSubagentEvent? subagentEvent = ParseSubagentEvent(lineNumber, normalizedType, root, data);
        if (subagentEvent is not null)
        {
            subagentEvents.Add(subagentEvent);
        }
    }

    private static List<ParsedToolRequest> ParseLineToolRequests(JsonElement data)
    {
        if (!data.TryGetProperty("toolRequests", out JsonElement requestsEl) || requestsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ParsedToolRequest> requests = [];
        int order = 0;
        foreach (JsonElement item in requestsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? toolCallId = SafeOptionalText(
                ReadJsonString(item, "toolCallId")
                ?? ReadJsonString(item, "id")
                ?? ReadJsonString(item, "callId"));
            string? name = SafeOptionalText(ReadJsonString(item, "name"));
            string? type = SafeOptionalText(ReadJsonString(item, "type"));
            string? mcpServerName = SafeOptionalText(ReadMcpServerName(item));
            string? mcpToolName = SafeOptionalText(ReadMcpToolName(item));
            ToolContentMeasurement? inputMeasurement = MeasureToolInput(item);

            requests.Add(new ParsedToolRequest(
                RequestOrder: order++,
                ToolCallId: toolCallId,
                Name: name,
                Type: type,
                McpServerName: mcpServerName,
                McpToolName: mcpToolName,
                InputMeasurement: inputMeasurement));
        }

        return requests;
    }

    private static void AttachSkillInvocations(
        CopilotCliToolTelemetry telemetry,
        List<AssistantMessageEntry> messageEntries,
        Dictionary<string, AssistantMessageEntry> messageByEventId,
        Dictionary<string, AssistantMessageEntry> messageByMessageId,
        Dictionary<string, AssistantMessageEntry> messageByToolCallId)
    {
        if (telemetry.SkillInvocations.Count == 0 || messageEntries.Count == 0)
        {
            return;
        }

        foreach (ParsedSkillInvoked skill in telemetry.SkillInvocations)
        {
            AssistantMessageEntry? target = ResolveParentMessage(
                lineNumber: skill.LineNumber,
                agentKey: skill.AgentKey,
                toolCallId: skill.ToolCallId,
                messageId: skill.MessageId,
                parentId: skill.ParentId,
                telemetry: telemetry,
                messageEntries: messageEntries,
                messageByEventId: messageByEventId,
                messageByMessageId: messageByMessageId,
                messageByToolCallId: messageByToolCallId);

            if (target is not null && target.SeenSkills.Add(skill.SkillName))
            {
                target.SkillsInvoked.Add(skill.SkillName);
            }
        }
    }

    private static List<CollectedRecord> BuildCorrelatedRecords(
        string sessionId,
        string harnessId,
        CopilotCliToolTelemetry telemetry,
        List<AssistantMessageEntry> messageEntries,
        Dictionary<string, AssistantMessageEntry> messageByEventId,
        Dictionary<string, AssistantMessageEntry> messageByMessageId,
        HashSet<string> seenRecordIds)
    {
        List<CollectedRecord> records = [];
        HashSet<string> consumedCallIds = new(StringComparer.Ordinal);
        Dictionary<int, List<CollectedRecord>> extraToolsByMessageLine = [];

        AddRequestedToolRecords(records, consumedCallIds, sessionId, harnessId, telemetry, messageEntries, seenRecordIds);
        AddOrphanToolRecords(
            records,
            consumedCallIds,
            extraToolsByMessageLine,
            sessionId,
            harnessId,
            telemetry,
            messageEntries,
            messageByEventId,
            messageByMessageId,
            seenRecordIds);

        return extraToolsByMessageLine.Count == 0
            ? records
            : MergeExtraTools(records, extraToolsByMessageLine, messageEntries);
    }

    private static void AddRequestedToolRecords(
        List<CollectedRecord> records,
        HashSet<string> consumedCallIds,
        string sessionId,
        string harnessId,
        CopilotCliToolTelemetry telemetry,
        List<AssistantMessageEntry> messageEntries,
        HashSet<string> seenRecordIds)
    {
        foreach (AssistantMessageEntry entry in messageEntries)
        {
            CollectedRecord finalizedLlm = entry.SkillsInvoked.Count == entry.Record.SkillsInvoked.Count
                ? entry.Record
                : entry.Record with { SkillsInvoked = [.. entry.SkillsInvoked] };
            records.Add(finalizedLlm);

            for (int i = 0; i < entry.ToolRequests.Count; i++)
            {
                ParsedToolRequest request = entry.ToolRequests[i];
                ParsedToolStart? start = null;
                ParsedToolComplete? complete = null;

                if (!string.IsNullOrEmpty(request.ToolCallId))
                {
                    _ = telemetry.StartByCallId.TryGetValue(request.ToolCallId, out start);
                    _ = telemetry.CompleteByCallId.TryGetValue(request.ToolCallId, out complete);
                }

                // Emit tool.invoke only when execution started or completed, matching Copilot CLI
                // execution telemetry and avoiding fabricated tool records for unstarted requests.
                if (start is null && complete is null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(request.ToolCallId))
                {
                    _ = consumedCallIds.Add(request.ToolCallId);
                }

                records.Add(BuildToolInvokeRecord(
                    parentRecord: finalizedLlm,
                    parentLineNumber: entry.LineNumber,
                    toolIndex: i,
                    sessionId: sessionId,
                    harnessId: harnessId,
                    request: request,
                    start: start,
                    complete: complete,
                    seenRecordIds: seenRecordIds));
            }
        }
    }

    private static void AddOrphanToolRecords(
        List<CollectedRecord> records,
        HashSet<string> consumedCallIds,
        Dictionary<int, List<CollectedRecord>> extraToolsByMessageLine,
        string sessionId,
        string harnessId,
        CopilotCliToolTelemetry telemetry,
        List<AssistantMessageEntry> messageEntries,
        Dictionary<string, AssistantMessageEntry> messageByEventId,
        Dictionary<string, AssistantMessageEntry> messageByMessageId,
        HashSet<string> seenRecordIds)
    {
        if (messageEntries.Count == 0)
        {
            return;
        }

        int fallbackIndex = 0;
        AddOrphanToolStarts(
            consumedCallIds,
            extraToolsByMessageLine,
            sessionId,
            harnessId,
            telemetry,
            messageEntries,
            messageByEventId,
            messageByMessageId,
            seenRecordIds,
            ref fallbackIndex);

        AddOrphanToolCompletes(
            consumedCallIds,
            extraToolsByMessageLine,
            sessionId,
            harnessId,
            telemetry,
            messageEntries,
            messageByEventId,
            messageByMessageId,
            seenRecordIds,
            ref fallbackIndex);
    }

    private static void AddOrphanToolStarts(
        HashSet<string> consumedCallIds,
        Dictionary<int, List<CollectedRecord>> extraToolsByMessageLine,
        string sessionId,
        string harnessId,
        CopilotCliToolTelemetry telemetry,
        List<AssistantMessageEntry> messageEntries,
        Dictionary<string, AssistantMessageEntry> messageByEventId,
        Dictionary<string, AssistantMessageEntry> messageByMessageId,
        HashSet<string> seenRecordIds,
        ref int fallbackIndex)
    {
        foreach (ParsedToolStart start in telemetry.ToolStarts)
        {
            if (!string.IsNullOrEmpty(start.ToolCallId) && !consumedCallIds.Add(start.ToolCallId))
            {
                continue;
            }

            ParsedToolComplete? complete = !string.IsNullOrEmpty(start.ToolCallId)
                && telemetry.CompleteByCallId.TryGetValue(start.ToolCallId, out ParsedToolComplete? matchedComplete)
                ? matchedComplete
                : null;

            AssistantMessageEntry? parent = ResolveParentMessage(
                lineNumber: start.LineNumber,
                agentKey: start.AgentKey,
                toolCallId: start.ToolCallId,
                messageId: start.MessageId,
                parentId: start.ParentId,
                telemetry: telemetry,
                messageEntries: messageEntries,
                messageByEventId: messageByEventId,
                messageByMessageId: messageByMessageId,
                messageByToolCallId: null);

            if (parent is null)
            {
                continue;
            }

            CollectedRecord orphanTool = BuildToolInvokeRecord(
                parentRecord: parent.Record,
                parentLineNumber: parent.LineNumber,
                toolIndex: parent.ToolRequests.Count + fallbackIndex++,
                sessionId: sessionId,
                harnessId: harnessId,
                request: null,
                start: start,
                complete: complete,
                seenRecordIds: seenRecordIds);

            AddExtraTool(extraToolsByMessageLine, parent.LineNumber, orphanTool);
        }
    }

    private static void AddOrphanToolCompletes(
        HashSet<string> consumedCallIds,
        Dictionary<int, List<CollectedRecord>> extraToolsByMessageLine,
        string sessionId,
        string harnessId,
        CopilotCliToolTelemetry telemetry,
        List<AssistantMessageEntry> messageEntries,
        Dictionary<string, AssistantMessageEntry> messageByEventId,
        Dictionary<string, AssistantMessageEntry> messageByMessageId,
        HashSet<string> seenRecordIds,
        ref int fallbackIndex)
    {
        foreach (ParsedToolComplete complete in telemetry.ToolCompletes)
        {
            if (!string.IsNullOrEmpty(complete.ToolCallId) && !consumedCallIds.Add(complete.ToolCallId))
            {
                continue;
            }

            AssistantMessageEntry? parent = ResolveParentMessage(
                lineNumber: complete.LineNumber,
                agentKey: complete.AgentKey,
                toolCallId: complete.ToolCallId,
                messageId: complete.MessageId,
                parentId: complete.ParentId,
                telemetry: telemetry,
                messageEntries: messageEntries,
                messageByEventId: messageByEventId,
                messageByMessageId: messageByMessageId,
                messageByToolCallId: null);

            if (parent is null)
            {
                continue;
            }

            CollectedRecord completeOnlyTool = BuildToolInvokeRecord(
                parentRecord: parent.Record,
                parentLineNumber: parent.LineNumber,
                toolIndex: parent.ToolRequests.Count + fallbackIndex++,
                sessionId: sessionId,
                harnessId: harnessId,
                request: null,
                start: null,
                complete: complete,
                seenRecordIds: seenRecordIds);

            AddExtraTool(extraToolsByMessageLine, parent.LineNumber, completeOnlyTool);
        }
    }

    private static List<CollectedRecord> MergeExtraTools(
        List<CollectedRecord> records,
        Dictionary<int, List<CollectedRecord>> extraToolsByMessageLine,
        List<AssistantMessageEntry> messageEntries)
    {
        List<CollectedRecord> merged = new(records.Count + extraToolsByMessageLine.Values.Sum(static l => l.Count));
        Dictionary<string, int> parentLineByRecordId = messageEntries.ToDictionary(
            static e => e.Record.RecordId,
            static e => e.LineNumber,
            StringComparer.Ordinal);

        for (int i = 0; i < records.Count; i++)
        {
            CollectedRecord current = records[i];
            merged.Add(current);

            if (current.Op == RecordOp.LlmInvoke
                && parentLineByRecordId.TryGetValue(current.RecordId, out int parentLine)
                && extraToolsByMessageLine.TryGetValue(parentLine, out List<CollectedRecord>? extras))
            {
                bool hasRequestChildren = i + 1 < records.Count
                    && records[i + 1].Op == RecordOp.ToolInvoke
                    && string.Equals(records[i + 1].ParentRecordId, current.RecordId, StringComparison.Ordinal);

                if (!hasRequestChildren)
                {
                    merged.AddRange(extras);
                }
            }
            else if (current.Op == RecordOp.ToolInvoke && current.ParentRecordId is not null)
            {
                bool isLastChildForParent = i + 1 >= records.Count
                    || records[i + 1].Op != RecordOp.ToolInvoke
                    || !string.Equals(records[i + 1].ParentRecordId, current.ParentRecordId, StringComparison.Ordinal);

                if (isLastChildForParent
                    && parentLineByRecordId.TryGetValue(current.ParentRecordId, out int ownerLine)
                    && extraToolsByMessageLine.TryGetValue(ownerLine, out List<CollectedRecord>? ownerExtras))
                {
                    merged.AddRange(ownerExtras);
                }
            }
        }

        return merged;
    }

    private static void AddExtraTool(
        Dictionary<int, List<CollectedRecord>> map,
        int parentLineNumber,
        CollectedRecord record)
    {
        if (!map.TryGetValue(parentLineNumber, out List<CollectedRecord>? list))
        {
            list = [];
            map[parentLineNumber] = list;
        }

        list.Add(record);
    }

    private static bool IsSubagentLifecycleEvent(string eventType) =>
        eventType is "subagent.started" or "subagent.completed" or "subagent.failed";

    private static ParsedSubagentEvent? ParseSubagentEvent(
        int lineNumber,
        string eventType,
        JsonElement root,
        JsonElement data)
    {
        string outcome = string.Equals(eventType, "subagent.started", StringComparison.Ordinal)
            ? "started"
            : string.Equals(eventType, "subagent.completed", StringComparison.Ordinal)
                ? "completed"
                : "failed";

        string? agentName = SafeOptionalText(
            ReadJsonString(data, "agentName")
            ?? ReadJsonString(data, "agentDisplayName")
            ?? ReadJsonString(root, "agentName")
            ?? ReadJsonString(root, "agentDisplayName"));
        string? toolCallId = SafeOptionalText(
            ReadJsonString(data, "toolCallId")
            ?? ReadJsonString(data, "callId")
            ?? ReadJsonString(root, "toolCallId"));
        string? timestamp = ReadJsonString(root, "timestamp") ?? ReadJsonString(data, "timestamp");

        string? errorMessage = null;
        string? errorCode = null;
        if (string.Equals(outcome, "failed", StringComparison.Ordinal))
        {
            (errorMessage, errorCode) = ParseSubagentError(data, root);
        }

        if (agentName is null && toolCallId is null)
        {
            return null;
        }

        return new ParsedSubagentEvent(
            LineNumber: lineNumber,
            Outcome: outcome,
            AgentName: agentName,
            ToolCallId: toolCallId,
            Timestamp: timestamp,
            ErrorMessage: errorMessage,
            ErrorCode: errorCode);
    }

    private static (string? ErrorMessage, string? ErrorCode) ParseSubagentError(JsonElement data, JsonElement root)
    {
        foreach (JsonElement container in new[] { data, root })
        {
            if (!TryGetNonNullProperty(container, "error", out JsonElement errorEl))
            {
                continue;
            }

            if (errorEl.ValueKind == JsonValueKind.Object)
            {
                return (
                    SafeOptionalText(ReadJsonString(errorEl, "message") ?? ReadJsonString(errorEl, "error")),
                    SafeOptionalText(ReadJsonString(errorEl, "code") ?? ReadJsonString(errorEl, "type")));
            }

            if (errorEl.ValueKind == JsonValueKind.String)
            {
                return (SafeOptionalText(errorEl.GetString()), null);
            }
        }

        return (null, null);
    }

    private static List<CollectedRecord> BuildSubagentRecords(
        string sessionId,
        string harnessId,
        CopilotCliToolTelemetry telemetry,
        List<CollectedRecord> correlatedRecords,
        HashSet<string> seenRecordIds)
    {
        if (telemetry.SubagentEvents.Count == 0)
        {
            return [];
        }

        Dictionary<string, CollectedRecord> toolByCallId = new(StringComparer.Ordinal);
        foreach (CollectedRecord record in correlatedRecords)
        {
            if (record.Op != RecordOp.ToolInvoke
                || record.Tool?.CallId is not { Length: > 0 } callId)
            {
                continue;
            }

            _ = toolByCallId.TryAdd(callId, record);
        }

        if (toolByCallId.Count == 0)
        {
            return [];
        }

        Dictionary<string, ParsedSubagentEvent> terminalByCallId = new(StringComparer.Ordinal);
        foreach (ParsedSubagentEvent evt in telemetry.SubagentEvents)
        {
            if (!string.Equals(evt.Outcome, "started", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(evt.ToolCallId))
            {
                _ = terminalByCallId.TryAdd(evt.ToolCallId, evt);
            }
        }

        List<CollectedRecord> agents = [];
        foreach (ParsedSubagentEvent started in telemetry.SubagentEvents)
        {
            if (!string.Equals(started.Outcome, "started", StringComparison.Ordinal))
            {
                continue;
            }

            string? spawnCallId = started.ToolCallId;
            if (string.IsNullOrEmpty(spawnCallId))
            {
                continue;
            }

            // DD-8: the parent is the tool.invoke named by the spawning call id. A started
            // run without a matching tool record, and a sub-agent envelope agentId with no
            // documented join to the run, both yield no link rather than an inferred one.
            if (!toolByCallId.TryGetValue(spawnCallId, out CollectedRecord? parent))
            {
                continue;
            }

            _ = terminalByCallId.TryGetValue(spawnCallId, out ParsedSubagentEvent? terminal);
            agents.Add(BuildAgentInvokeRecord(sessionId, harnessId, parent, started, terminal, seenRecordIds));
        }

        return agents;
    }

    private static CollectedRecord BuildAgentInvokeRecord(
        string sessionId,
        string harnessId,
        CollectedRecord parentTool,
        ParsedSubagentEvent started,
        ParsedSubagentEvent? terminal,
        HashSet<string> seenRecordIds)
    {
        string agentName = started.AgentName ?? "subagent";
        string spawnCallId = started.ToolCallId ?? string.Empty;
        bool? succeeded = terminal is null
            ? null
            : string.Equals(terminal.Outcome, "completed", StringComparison.Ordinal);
        string? errorMessage = terminal is { Outcome: "failed" }
            ? SafeOptionalText(terminal.ErrorMessage) ?? "Agent invocation failed."
            : null;

        DateTimeOffset startedAt = TryParseUtcTimestamp(started.Timestamp, out DateTimeOffset parsedStart)
            ? parsedStart
            : parentTool.StartedAt;

        DateTimeOffset? endedAt = null;
        long? durationMs = null;
        if (terminal is not null
            && TryParseUtcTimestamp(terminal.Timestamp, out DateTimeOffset parsedEnd)
            && parsedEnd >= startedAt)
        {
            endedAt = parsedEnd;
            durationMs = (long)Math.Round((parsedEnd - startedAt).TotalMilliseconds);
        }

        RecordStatus status = terminal is null
            ? RecordStatus.Unset
            : succeeded == true ? RecordStatus.Ok : RecordStatus.Error;

        string recordId = BuildUniqueAgentRecordId(parentTool.RecordId, spawnCallId, agentName, seenRecordIds);
        CollectedAgentInvocation agent = new(
            AgentName: agentName,
            SpawningCallId: spawnCallId,
            Succeeded: succeeded,
            Error: errorMessage);

        return new CollectedRecord(
            RecordId: recordId,
            HarnessId: harnessId,
            Op: RecordOp.AgentInvoke,
            Kind: RecordKind.Internal,
            Name: agentName,
            StartedAt: startedAt,
            Status: status,
            SessionKey: sessionId,
            ParentRecordId: parentTool.RecordId,
            TraceId: parentTool.TraceId,
            Model: null,
            EndedAt: endedAt,
            ReportedDurationMs: durationMs,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: agent,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(
                ErrorMessage: errorMessage,
                EditFailures: null,
                CorrectionRule: null,
                DeclaredContextWindow: null));
    }

    private static string BuildUniqueAgentRecordId(
        string parentRecordId,
        string spawnCallId,
        string agentName,
        HashSet<string> seenRecordIds)
    {
        string hashInput = string.Create(
            CultureInfo.InvariantCulture,
            $"subagent:{spawnCallId}:{agentName}");
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        string agentHash = Convert.ToHexStringLower(hashBytes)[..12];
        string candidate = string.Create(CultureInfo.InvariantCulture, $"{parentRecordId}-a{agentHash}");

        if (seenRecordIds.Add(candidate))
        {
            return candidate;
        }

        int suffix = 2;
        while (true)
        {
            string disambiguated = string.Create(CultureInfo.InvariantCulture, $"{candidate}:{suffix}");
            if (seenRecordIds.Add(disambiguated))
            {
                return disambiguated;
            }

            suffix++;
        }
    }

    private static AssistantMessageEntry? ResolveParentMessage(
        int lineNumber,
        string agentKey,
        string? toolCallId,
        string? messageId,
        string? parentId,
        CopilotCliToolTelemetry telemetry,
        List<AssistantMessageEntry> messageEntries,
        Dictionary<string, AssistantMessageEntry> messageByEventId,
        Dictionary<string, AssistantMessageEntry> messageByMessageId,
        Dictionary<string, AssistantMessageEntry>? messageByToolCallId)
    {
        if (toolCallId is not null
            && messageByToolCallId is not null
            && messageByToolCallId.TryGetValue(toolCallId, out AssistantMessageEntry? byCallId))
        {
            return byCallId;
        }

        if (messageId is not null && messageByMessageId.TryGetValue(messageId, out AssistantMessageEntry? byMsgId))
        {
            return byMsgId;
        }

        if (parentId is not null)
        {
            if (messageByEventId.TryGetValue(parentId, out AssistantMessageEntry? byEventId))
            {
                return byEventId;
            }

            if (messageByMessageId.TryGetValue(parentId, out AssistantMessageEntry? byParentMsgId))
            {
                return byParentMsgId;
            }

            if (messageByToolCallId is not null
                && telemetry.StartEventIdToCallId.TryGetValue(parentId, out string? linkedCallId)
                && messageByToolCallId.TryGetValue(linkedCallId, out AssistantMessageEntry? byStartCallId))
            {
                return byStartCallId;
            }
        }

        AssistantMessageEntry? nearestPrecedingSameAgent = null;
        AssistantMessageEntry? nearestFollowingSameAgent = null;
        foreach (AssistantMessageEntry candidate in messageEntries)
        {
            if (!string.Equals(candidate.AgentKey, agentKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.LineNumber < lineNumber)
            {
                nearestPrecedingSameAgent = candidate;
            }
            else if (nearestFollowingSameAgent is null)
            {
                nearestFollowingSameAgent = candidate;
            }
        }

        return nearestPrecedingSameAgent ?? nearestFollowingSameAgent;
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parentRecord,
        int parentLineNumber,
        int toolIndex,
        string sessionId,
        string harnessId,
        ParsedToolRequest? request,
        ParsedToolStart? start,
        ParsedToolComplete? complete,
        HashSet<string> seenRecordIds)
    {
        string safeCallId = SafeOptionalText(request?.ToolCallId ?? start?.ToolCallId ?? complete?.ToolCallId)
            ?? string.Create(CultureInfo.InvariantCulture, $"call-{parentLineNumber}-{toolIndex}");
        string safeToolName = SafeOptionalText(request?.Name ?? start?.ToolName ?? complete?.ToolName) ?? "tool";

        (string? mcpServerName, string? mcpToolName) = ResolveMcpAttribution(safeToolName, request, start, complete);

        ToolContentMeasurement? inputMeasurement = request?.InputMeasurement ?? start?.InputMeasurement;
        CollectedToolContent? inputContent = inputMeasurement?.ToToolContent(includeFingerprint: true);

        string childRecordId = BuildUniqueToolRecordId(
            parentRecord.RecordId,
            safeCallId,
            safeToolName,
            inputMeasurement,
            seenRecordIds);

        ResolveToolTiming(parentRecord, start, complete, out DateTimeOffset startedAt, out DateTimeOffset? endedAt, out long? durationMs);

        RecordStatus status = complete is null
            ? RecordStatus.Unset
            : complete.Succeeded
                ? RecordStatus.Ok
                : RecordStatus.Error;

        (CollectedToolContent? resultContent, List<CollectedContentPart> parts) = BuildToolResultParts(complete, mcpServerName);

        bool? succeeded = complete is null ? null : complete.Succeeded;
        CollectedError? error = complete is { Succeeded: false }
            ? new CollectedError(SafeToolErrorMessage(complete.ErrorMessage), complete.ErrorCode)
            : null;
        string? signalErrorMessage = error?.Message;
        string? agentInstanceId = ResolveToolAgentInstanceId(parentRecord, start, complete);

        CollectedToolCall collectedTool = new(
            CallId: safeCallId,
            ToolName: safeToolName,
            McpServerName: mcpServerName,
            McpToolName: mcpToolName,
            Input: inputContent,
            Result: resultContent,
            Succeeded: succeeded,
            Error: error,
            ExitCode: complete?.ExitCode);

        return new CollectedRecord(
            RecordId: childRecordId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: safeToolName,
            StartedAt: startedAt,
            Status: status,
            SessionKey: sessionId,
            ParentRecordId: parentRecord.RecordId,
            TraceId: parentRecord.TraceId,
            Model: null,
            EndedAt: endedAt,
            ReportedDurationMs: durationMs,
            Tokens: [],
            ReportedCost: null,
            Parts: parts,
            Tool: collectedTool,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: agentInstanceId,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(signalErrorMessage, null, null, null));
    }

    private static void ResolveToolTiming(
        CollectedRecord parentRecord,
        ParsedToolStart? start,
        ParsedToolComplete? complete,
        out DateTimeOffset startedAt,
        out DateTimeOffset? endedAt,
        out long? durationMs)
    {
        startedAt = TryParseUtcTimestamp(start?.Timestamp, out DateTimeOffset parsedStart)
            ? parsedStart
            : parentRecord.StartedAt;

        endedAt = null;
        durationMs = null;
        if (complete is null)
        {
            return;
        }

        bool hasEndTimestamp = TryParseUtcTimestamp(complete.Timestamp, out DateTimeOffset parsedEnd);
        if (complete.DurationMs is >= 0)
        {
            durationMs = complete.DurationMs.Value;
        }
        else if (hasEndTimestamp && parsedEnd >= startedAt)
        {
            durationMs = (long)Math.Round((parsedEnd - startedAt).TotalMilliseconds);
        }

        if (hasEndTimestamp && parsedEnd >= startedAt)
        {
            endedAt = parsedEnd;
        }
        else if (durationMs is >= 0)
        {
            endedAt = startedAt.AddMilliseconds(durationMs.Value);
        }
    }

    private static (CollectedToolContent? ResultContent, List<CollectedContentPart> Parts) BuildToolResultParts(
        ParsedToolComplete? complete,
        string? mcpServerName)
    {
        List<CollectedContentPart> parts = [];
        if (complete?.ResultMeasurement is not { } resultMeasurement)
        {
            return (null, parts);
        }

        CollectedToolContent resultContent = resultMeasurement.ToToolContent(includeFingerprint: false);

        string? plainText = resultMeasurement.IsDocument ? null : resultMeasurement.PlainText;
        CollectedWithheldText? withheld = resultMeasurement.IsDocument ? resultMeasurement.ToWithheldText() : null;
        long? originalBytes = resultMeasurement.IsDocument ? null : resultMeasurement.OriginalUtf8BytesWhenTruncated;

        parts.Add(new CollectedContentPart(
            Position: 0,
            Bucket: ContentBucket.ToolResultContent,
            PlainText: plainText,
            Withheld: withheld,
            ReportedTokens: null,
            Server: mcpServerName,
            Role: "tool",
            Resident: null,
            OriginalUtf8BytesWhenTruncated: originalBytes));

        return (resultContent, parts);
    }

    private static string? ResolveToolAgentInstanceId(
        CollectedRecord parentRecord,
        ParsedToolStart? start,
        ParsedToolComplete? complete) =>
        SafeOptionalText(
            start is { AgentKey.Length: > 0 } ? start.AgentKey :
            complete is { AgentKey.Length: > 0 } ? complete.AgentKey :
            null) ?? parentRecord.AgentInstanceId;

    private static (string? ServerName, string? ToolName) ResolveMcpAttribution(
        string safeToolName,
        ParsedToolRequest? request,
        ParsedToolStart? start,
        ParsedToolComplete? complete)
    {
        string? mcpServer = request?.McpServerName ?? start?.McpServerName ?? complete?.McpServerName;
        string? mcpTool = request?.McpToolName ?? start?.McpToolName ?? complete?.McpToolName;

        if (mcpServer is null && mcpTool is null)
        {
            if (safeToolName.StartsWith("mcp__", StringComparison.Ordinal))
            {
                (mcpServer, mcpTool) = ParsePrefixedMcpAttribution(safeToolName);
            }
            else if (string.Equals(request?.Type, "mcp", StringComparison.OrdinalIgnoreCase))
            {
                (mcpServer, mcpTool) = ParseTypedMcpAttribution(safeToolName);
            }
        }

        if (mcpServer is not null && mcpTool is null)
        {
            mcpTool = DeriveMcpToolName(safeToolName, mcpServer);
        }
        else if (mcpServer is null && mcpTool is not null)
        {
            mcpTool = null;
        }

        return (mcpServer, mcpTool);
    }

    private static (string? ServerName, string? ToolName) ParsePrefixedMcpAttribution(string safeToolName)
    {
        int separatorIndex = safeToolName.IndexOf("__", 5, StringComparison.Ordinal);
        if (separatorIndex > 5 && separatorIndex + 2 < safeToolName.Length)
        {
            return (
                SafeOptionalText(safeToolName[5..separatorIndex]),
                SafeOptionalText(safeToolName[(separatorIndex + 2)..]));
        }

        return (null, null);
    }

    private static (string? ServerName, string? ToolName) ParseTypedMcpAttribution(string safeToolName)
    {
        int doubleUnderscore = safeToolName.IndexOf("__", StringComparison.Ordinal);
        if (doubleUnderscore > 0 && doubleUnderscore + 2 < safeToolName.Length)
        {
            return (
                SafeOptionalText(safeToolName[..doubleUnderscore]),
                SafeOptionalText(safeToolName[(doubleUnderscore + 2)..]));
        }

        int sep = safeToolName.IndexOfAny(['/', ':']);
        if (sep > 0 && sep + 1 < safeToolName.Length)
        {
            return (
                SafeOptionalText(safeToolName[..sep]),
                SafeOptionalText(safeToolName[(sep + 1)..]));
        }

        return (null, null);
    }

    private static string DeriveMcpToolName(string safeToolName, string mcpServer)
    {
        string[] prefixes =
        [
            $"{mcpServer}-",
            $"{mcpServer}_",
            $"{mcpServer}/",
            $"{mcpServer}:",
            $"{mcpServer.Replace('-', '_')}-",
            $"{mcpServer.Replace('-', '_')}_",
        ];

        foreach (string prefix in prefixes)
        {
            if (safeToolName.StartsWith(prefix, StringComparison.Ordinal) && safeToolName.Length > prefix.Length)
            {
                return safeToolName[prefix.Length..];
            }
        }

        return safeToolName;
    }

    private static string BuildUniqueToolRecordId(
        string parentRecordId,
        string safeCallId,
        string safeToolName,
        ToolContentMeasurement? inputMeasurement,
        HashSet<string> seenRecordIds)
    {
        string hashInput = string.Create(
            CultureInfo.InvariantCulture,
            $"{safeCallId}:{safeToolName}:{inputMeasurement?.Sha256Fingerprint ?? string.Empty}");
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];
        string candidate = string.Create(CultureInfo.InvariantCulture, $"{parentRecordId}-t{toolHash}");

        if (seenRecordIds.Add(candidate))
        {
            return candidate;
        }

        int suffix = 2;
        while (true)
        {
            string disambiguated = string.Create(CultureInfo.InvariantCulture, $"{candidate}:{suffix}");
            if (seenRecordIds.Add(disambiguated))
            {
                return disambiguated;
            }

            suffix++;
        }
    }

    private static ToolContentMeasurement? MeasureToolInput(JsonElement container)
    {
        if (!TryGetNonNullProperty(container, "arguments", out JsonElement inputEl)
            && !TryGetNonNullProperty(container, "input", out inputEl))
        {
            return null;
        }

        return ToolContentMeasurer.Measure(inputEl);
    }

    private static ToolContentMeasurement? MeasureToolResult(JsonElement data)
    {
        if (!TryGetNonNullProperty(data, "result", out JsonElement resultEl)
            && !TryGetNonNullProperty(data, "output", out resultEl))
        {
            return null;
        }

        if (resultEl.ValueKind == JsonValueKind.Object)
        {
            if (TryGetNonNullProperty(resultEl, "content", out JsonElement contentEl))
            {
                return ToolContentMeasurer.Measure(contentEl);
            }

            if (TryGetNonNullProperty(resultEl, "detailedContent", out JsonElement detailedEl))
            {
                return ToolContentMeasurer.Measure(detailedEl);
            }

            if (TryGetNonNullProperty(resultEl, "text", out JsonElement textEl))
            {
                return ToolContentMeasurer.Measure(textEl);
            }

            if (TryGetNonNullProperty(resultEl, "output", out JsonElement nestedOutputEl))
            {
                return ToolContentMeasurer.Measure(nestedOutputEl);
            }
        }

        return ToolContentMeasurer.Measure(resultEl);
    }

    private static (bool Succeeded, string? ErrorMessage, string? ErrorCode) ParseCompletionOutcome(
        JsonElement data,
        ToolContentMeasurement? resultMeasurement)
    {
        bool? explicitSuccess = ReadOptionalBool(data, "success");
        bool? explicitIsError = ReadOptionalBool(data, "isError") ?? ReadOptionalBool(data, "is_error");
        bool hasErrorProperty = TryGetNonNullProperty(data, "error", out JsonElement errorEl);

        bool succeeded = explicitSuccess
            ?? (explicitIsError.HasValue ? !explicitIsError.Value : !hasErrorProperty);

        if (succeeded)
        {
            return (true, null, null);
        }

        string? errorMessage = null;
        string? errorCode = null;
        if (hasErrorProperty)
        {
            if (errorEl.ValueKind == JsonValueKind.Object)
            {
                errorMessage = SafeOptionalText(ReadJsonString(errorEl, "message") ?? ReadJsonString(errorEl, "error"));
                errorCode = SafeOptionalText(ReadJsonString(errorEl, "code") ?? ReadJsonString(errorEl, "type"));
            }
            else if (errorEl.ValueKind == JsonValueKind.String)
            {
                errorMessage = SafeOptionalText(errorEl.GetString());
            }
        }

        if (errorMessage is null && resultMeasurement is { IsDocument: false })
        {
            errorMessage = SafeOptionalText(resultMeasurement.PlainText);
        }

        return (false, errorMessage ?? "Tool execution failed.", errorCode);
    }

    private static bool TryGetNonNullProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value)
            && value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadMcpServerName(JsonElement container)
    {
        string? direct = ReadJsonString(container, "mcpServerName")
            ?? ReadJsonString(container, "serverName")
            ?? ReadJsonString(container, "mcp_server_name")
            ?? ReadJsonString(container, "server");
        if (direct is not null)
        {
            return direct;
        }

        if (container.TryGetProperty("mcp", out JsonElement mcpEl) && mcpEl.ValueKind == JsonValueKind.Object)
        {
            return ReadJsonString(mcpEl, "serverName") ?? ReadJsonString(mcpEl, "server");
        }

        return null;
    }

    private static string? ReadMcpToolName(JsonElement container)
    {
        string? direct = ReadJsonString(container, "mcpToolName")
            ?? ReadJsonString(container, "serverToolName")
            ?? ReadJsonString(container, "mcp_tool_name");
        if (direct is not null)
        {
            return direct;
        }

        if (container.TryGetProperty("mcp", out JsonElement mcpEl) && mcpEl.ValueKind == JsonValueKind.Object)
        {
            return ReadJsonString(mcpEl, "toolName") ?? ReadJsonString(mcpEl, "name");
        }

        return null;
    }

    private static string? ReadJsonString(JsonElement element, string propertyName) =>
        HarnessJson.ReadString(element, propertyName);

    private static bool? ReadOptionalBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static long? ReadOptionalLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt64(out long value) ? value : null;
    }

    private static long? ReadOptionalNonNegativeLong(JsonElement element, string propertyName) =>
        HarnessJson.ReadOptionalNonNegativeLong(element, propertyName);

    private static long? ReadOptionalLongFromNested(JsonElement element, string nestedProperty, string propertyName)
    {
        if (!element.TryGetProperty(nestedProperty, out JsonElement nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadOptionalLong(nested, propertyName);
    }

    private static bool TryParseUtcTimestamp(string? rawTimestamp, out DateTimeOffset parsed) =>
        HarnessJson.TryParseTimestamp(rawTimestamp, out parsed);

    private sealed class CopilotCliMappingState
    {
        internal CopilotCliMappingState(
            NativeSourceUnit unit,
            DateTimeOffset importedAtUtc,
            string sessionId,
            CopilotCliJournalDocument document)
        {
            Unit = unit;
            ImportedAtUtc = importedAtUtc;
            SessionId = sessionId;
            Document = document;
            SessionModel = SafeOptionalText(document.InitialModel);
            SessionTitle = SafeOptionalText(document.Title);
            WorkingDirectory = SafeOptionalText(document.InitialContext?.WorkingDirectory);
            Repository = ResolveRepositoryName(document.InitialContext);
            Branch = SafeOptionalText(document.InitialContext?.Branch);
        }

        internal NativeSourceUnit Unit { get; }

        internal DateTimeOffset ImportedAtUtc { get; }

        internal string SessionId { get; }

        internal CopilotCliJournalDocument Document { get; }

        internal string? SessionModel { get; set; }

        internal string? SessionTitle { get; set; }

        internal string? WorkingDirectory { get; set; }

        internal string? Repository { get; set; }

        internal string? Branch { get; set; }

        internal long? SessionDeclaredContextWindow { get; set; }

        internal Dictionary<string, string> AgentModels { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, string> LatestSystemPrompts { get; } = new(StringComparer.Ordinal);

        internal List<IReadOnlyList<CopilotCliModelUsage>> ShutdownSnapshots { get; } = [];

        internal Dictionary<string, List<string>> PendingUserMessages { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, List<string>> PendingReasonings { get; } = new(StringComparer.Ordinal);

        internal List<AssistantMessageEntry> MessageEntries { get; } = [];

        internal Dictionary<string, AssistantMessageEntry> MessageByEventId { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, AssistantMessageEntry> MessageByMessageId { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, AssistantMessageEntry> MessageByToolCallId { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> SeenRecordIds { get; } = new(StringComparer.Ordinal);
    }

    private record ParsedToolRequest(
        int RequestOrder,
        string? ToolCallId,
        string? Name,
        string? Type,
        string? McpServerName,
        string? McpToolName,
        ToolContentMeasurement? InputMeasurement);

    private record ParsedToolStart(
        int LineNumber,
        string? EventId,
        string? ParentId,
        string? MessageId,
        string AgentKey,
        string? ToolCallId,
        string? ToolName,
        string? McpServerName,
        string? McpToolName,
        string? Timestamp,
        ToolContentMeasurement? InputMeasurement);

    private record ParsedToolComplete(
        int LineNumber,
        string? EventId,
        string? ParentId,
        string? MessageId,
        string AgentKey,
        string? ToolCallId,
        string? ToolName,
        string? McpServerName,
        string? McpToolName,
        string? Timestamp,
        long? DurationMs,
        bool Succeeded,
        ToolContentMeasurement? ResultMeasurement,
        string? ErrorMessage,
        string? ErrorCode,
        long? ExitCode);

    private record ParsedSkillInvoked(
        int LineNumber,
        string SkillName,
        string? ToolCallId,
        string? MessageId,
        string? ParentId,
        string AgentKey);

    private record ParsedSubagentEvent(
        int LineNumber,
        string Outcome,
        string? AgentName,
        string? ToolCallId,
        string? Timestamp,
        string? ErrorMessage,
        string? ErrorCode);

    private record CopilotCliToolTelemetry(
        Dictionary<int, List<ParsedToolRequest>> ToolRequestsByLine,
        List<ParsedToolStart> ToolStarts,
        Dictionary<string, ParsedToolStart> StartByCallId,
        Dictionary<string, string> StartEventIdToCallId,
        List<ParsedToolComplete> ToolCompletes,
        Dictionary<string, ParsedToolComplete> CompleteByCallId,
        List<ParsedSkillInvoked> SkillInvocations,
        List<ParsedSubagentEvent> SubagentEvents);

    private record AssistantMessageEntry(
        int LineNumber,
        string? EventId,
        string? MessageId,
        string AgentKey,
        CollectedRecord Record,
        IReadOnlyList<ParsedToolRequest> ToolRequests,
        List<string> SkillsInvoked,
        HashSet<string> SeenSkills);
}
