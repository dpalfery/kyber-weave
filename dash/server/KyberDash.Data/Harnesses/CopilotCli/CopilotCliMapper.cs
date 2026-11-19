using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    IReadOnlyList<CopilotCliToolRequest> ToolRequests);

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

        string? sessionModel = SafeOptionalText(document.InitialModel);
        string? sessionTitle = SafeOptionalText(document.Title);
        string? workingDirectory = SafeOptionalText(document.InitialContext?.WorkingDirectory);
        string? repository = ResolveRepositoryName(document.InitialContext);
        string? branch = SafeOptionalText(document.InitialContext?.Branch);
        long? sessionDeclaredContextWindow = null;

        Dictionary<string, string> agentModels = new(StringComparer.Ordinal);
        Dictionary<string, string> latestSystemPrompts = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> pendingUserMessages = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> pendingReasonings = new(StringComparer.Ordinal);

        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (CopilotCliJournalEvent evt in document.Events)
        {
            string agentKey = string.IsNullOrWhiteSpace(evt.AgentId) ? RootAgentKey : evt.AgentId.Trim();

            if (evt.DeclaredContextWindow is > 0)
            {
                sessionDeclaredContextWindow = evt.DeclaredContextWindow.Value;
            }

            if (IsSessionStart(evt.EventType))
            {
                string? startModel = SafeOptionalText(evt.Model);
                if (startModel is not null)
                {
                    sessionModel = startModel;
                }

                string? startTitle = SafeOptionalText(evt.Title);
                if (startTitle is not null)
                {
                    sessionTitle = startTitle;
                }

                ApplyContextUpdate(evt.Context, ref workingDirectory, ref repository, ref branch);
                continue;
            }

            if (IsModelChange(evt.EventType))
            {
                string? newModel = SafeOptionalText(evt.Model);
                if (newModel is not null)
                {
                    if (agentKey.Length == 0)
                    {
                        sessionModel = newModel;
                    }
                    else
                    {
                        agentModels[agentKey] = newModel;
                    }
                }

                continue;
            }

            if (IsContextChange(evt.EventType))
            {
                ApplyContextUpdate(evt.Context, ref workingDirectory, ref repository, ref branch);
                string? updatedTitle = SafeOptionalText(evt.Title);
                if (updatedTitle is not null)
                {
                    sessionTitle = updatedTitle;
                }

                continue;
            }

            if (string.Equals(evt.EventType, "system.message", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(evt.Content))
                {
                    latestSystemPrompts[agentKey] = evt.Content;
                }

                continue;
            }

            if (string.Equals(evt.EventType, "user.message", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(evt.Content))
                {
                    AddPendingMessage(pendingUserMessages, agentKey, evt.Content);
                }

                continue;
            }

            if (string.Equals(evt.EventType, "assistant.reasoning", StringComparison.Ordinal))
            {
                string? reasoning = !string.IsNullOrEmpty(evt.ReasoningText) ? evt.ReasoningText : evt.Content;
                if (!string.IsNullOrEmpty(reasoning))
                {
                    AddPendingMessage(pendingReasonings, agentKey, reasoning);
                }

                continue;
            }

            if (!string.Equals(evt.EventType, "assistant.message", StringComparison.Ordinal))
            {
                continue;
            }

            string? activeModel = SafeOptionalText(evt.Model)
                ?? (agentKey.Length > 0 && agentModels.TryGetValue(agentKey, out string? agentModel) ? agentModel : null)
                ?? sessionModel;

            List<CollectedContentPart> parts = [];
            int position = 0;
            string? correctionRule = null;

            if (latestSystemPrompts.TryGetValue(agentKey, out string? systemPrompt))
            {
                parts.Add(BuildContentPart(position++, ContentBucket.SystemPrompt, systemPrompt, "system"));
            }

            if (pendingUserMessages.TryGetValue(agentKey, out List<string>? userMessages))
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

                    parts.Add(BuildContentPart(position++, ContentBucket.UserMessage, userMessage, "user"));
                }

                userMessages.Clear();
            }

            if (pendingReasonings.TryGetValue(agentKey, out List<string>? reasonings))
            {
                foreach (string reasoning in reasonings)
                {
                    parts.Add(BuildContentPart(position++, ContentBucket.Reasoning, reasoning, "assistant"));
                }

                reasonings.Clear();
            }

            if (!string.IsNullOrEmpty(evt.ReasoningText))
            {
                parts.Add(BuildContentPart(position++, ContentBucket.Reasoning, evt.ReasoningText, "assistant"));
            }

            if (!string.IsNullOrEmpty(evt.Content))
            {
                parts.Add(BuildContentPart(position++, ContentBucket.ConversationHistory, evt.Content, "assistant"));
            }

            List<CollectedTokenCount> tokens = BuildTokens(evt);
            List<string> skillsInvoked = ExtractInvokedSkills(evt.ToolRequests);
            DateTimeOffset startedAt = ParseTimestamp(evt.Timestamp, unit.LastWriteUtc, importedAtUtc);
            long? durationMs = evt.DurationMs is >= 0 ? evt.DurationMs.Value : null;
            DateTimeOffset? endedAt = durationMs is > 0 ? startedAt.AddMilliseconds(durationMs.Value) : null;

            string recordId = BuildUniqueRecordId(sessionId, evt, seenRecordIds);
            string recordName = activeModel is not null ? $"copilot:{activeModel}" : "copilot:assistant.message";
            string? agentInstanceId = agentKey.Length > 0 ? SafeOptionalText(agentKey) : null;
            long? recordContextWindow = evt.DeclaredContextWindow is > 0
                ? evt.DeclaredContextWindow.Value
                : sessionDeclaredContextWindow;

            records.Add(new CollectedRecord(
                RecordId: recordId,
                HarnessId: HarnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Internal,
                Name: recordName,
                StartedAt: startedAt,
                Status: RecordStatus.Ok,
                SessionKey: sessionId,
                ParentRecordId: null,
                TraceId: $"{CanonSynth.SynthSpanPrefix}{HarnessId}:{sessionId}",
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
                ReportedOutputTokens: null));
        }

        List<CollectedSession> sessions = [];
        if (records.Count > 0 || document.Events.Count > 0)
        {
            CollectedWorkspace? workspace = workingDirectory is not null || repository is not null || branch is not null
                ? new CollectedWorkspace(workingDirectory, repository, branch)
                : null;

            sessions.Add(new CollectedSession(
                SessionKey: sessionId,
                HarnessId: HarnessId,
                NativeSessionId: sessionId,
                Workspace: workspace,
                Title: sessionTitle,
                ReportedUsage: []));
        }

        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId, importedAtUtc);
        return new CollectedBatch(source, sessions, records, problems);
    }

    private static bool IsSessionStart(string eventType) =>
        string.Equals(eventType, "session.start", StringComparison.Ordinal);

    private static bool IsModelChange(string eventType) =>
        eventType is "session.model_change" or "session.model_changed" or "model_change" or "model.change";

    private static bool IsContextChange(string eventType) =>
        eventType is "session.context_changed"
            or "session.context_change"
            or "context.changed"
            or "context.change"
            or "session.resume"
            or "session.info";

    private static void ApplyContextUpdate(
        CopilotCliContextInfo? context,
        ref string? workingDirectory,
        ref string? repository,
        ref string? branch)
    {
        if (context is null)
        {
            return;
        }

        string? nextWorkingDir = SafeOptionalText(context.WorkingDirectory);
        if (nextWorkingDir is not null)
        {
            workingDirectory = nextWorkingDir;
        }

        string? nextRepo = ResolveRepositoryName(context);
        if (nextRepo is not null)
        {
            repository = nextRepo;
        }

        string? nextBranch = SafeOptionalText(context.Branch);
        if (nextBranch is not null)
        {
            branch = nextBranch;
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

    private static CollectedContentPart BuildContentPart(
        int position,
        ContentBucket bucket,
        string rawText,
        string role)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(rawText);
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
