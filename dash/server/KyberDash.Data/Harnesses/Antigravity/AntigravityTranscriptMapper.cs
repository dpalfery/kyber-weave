using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Antigravity;

/// <summary>
/// Native tool invocation decoded from a <c>PLANNER_RESPONSE.tool_calls</c> entry in an
/// Antigravity conversation <c>transcript.jsonl</c>.
/// </summary>
/// <param name="CallId">Deterministic correlation identifier for this tool invocation.</param>
/// <param name="ToolName">Invoked tool name.</param>
/// <param name="RawArgumentsJson">Canonical JSON object of decoded tool arguments prior to measurement and withholding.</param>
/// <param name="InferredSkillName">Skill identifier inferred when <see cref="ToolName"/> is <c>view_file</c> targeting <c>SKILL.md</c>.</param>
public record AntigravityTranscriptToolCall(
    string CallId,
    string ToolName,
    string RawArgumentsJson,
    string? InferredSkillName);

/// <summary>
/// Native transcript row parsed from an Antigravity <c>transcript.jsonl</c> line.
/// </summary>
/// <param name="StepIndex">Zero-based transcript <c>step_index</c> used for segment bounds and span identity.</param>
/// <param name="StepType">Native row type (<c>USER_INPUT</c>, <c>PLANNER_RESPONSE</c>, or <c>GENERIC</c>).</param>
/// <param name="Source">Optional native step source label (<c>USER_EXPLICIT</c>, <c>MODEL</c>, or <c>SYSTEM</c>).</param>
/// <param name="Status">Optional native step completion status (<c>DONE</c>, <c>ERROR</c>, etc.).</param>
/// <param name="CreatedAt">Optional ISO-8601 UTC timestamp (<c>created_at</c>).</param>
/// <param name="Content">Optional plain-text step payload (<c>content</c>).</param>
/// <param name="ToolCalls">Tool calls carried on a <c>PLANNER_RESPONSE</c> step.</param>
public record AntigravityTranscriptStep(
    long StepIndex,
    string StepType,
    string? Source,
    string? Status,
    string? CreatedAt,
    string? Content,
    IReadOnlyList<AntigravityTranscriptToolCall> ToolCalls);

/// <summary>
/// Segment of transcript steps owned by a single <c>agy-step:&lt;last_step_index&gt;</c> generation.
/// </summary>
/// <param name="Steps">Transcript steps with <c>step_index</c> in <c>(from, to]</c>.</param>
/// <param name="Covered">True when the transcript contains steps covering both sides of the segment start.</param>
/// <param name="Result">Output text of a paired <c>GENERIC</c> step when the segment holds exactly one tool call.</param>
public record AntigravityTranscriptSegment(
    IReadOnlyList<AntigravityTranscriptStep> Steps,
    bool Covered,
    string? Result);

/// <summary>
/// Native snapshot line parsed from <c>cache/antigravity-statusline.jsonl</c> for <c>antigravity-cli</c>.
/// </summary>
/// <param name="Timestamp">Optional ISO-8601 UTC snapshot timestamp (<c>at</c> or <c>timestamp</c>).</param>
/// <param name="ConversationId">Required Antigravity conversation identifier (<c>conversation_id</c> or <c>conversationId</c>).</param>
/// <param name="SessionId">Optional secondary statusline <c>session_id</c> / <c>sessionId</c> (dropped; <see cref="ConversationId"/> anchors the session).</param>
/// <param name="Model">Resolved model identifier (<c>model</c>, <c>model.id</c>, or <c>model.display_name</c>).</param>
/// <param name="ModelDisplayName">Optional <c>model.display_name</c> when <c>model</c> is an object (dropped unless used as model fallback).</param>
/// <param name="InputTokens">Cumulative or snapshot input token count.</param>
/// <param name="OutputTokens">Cumulative or snapshot aggregate output token count.</param>
/// <param name="CacheCreationInputTokens">Cumulative or snapshot cache-creation input token count.</param>
/// <param name="CacheReadInputTokens">Cumulative or snapshot cache-read input token count.</param>
/// <param name="ContextWindowSize">Optional unmapped <c>context_window.context_window_size</c> snapshot field.</param>
/// <param name="Cwd">Optional unmapped <c>cwd</c> workspace path withheld from persisted storage.</param>
public record AntigravityStatusLineEntry(
    string? Timestamp,
    string ConversationId,
    string? SessionId,
    string Model,
    string? ModelDisplayName,
    long InputTokens,
    long OutputTokens,
    long CacheCreationInputTokens,
    long CacheReadInputTokens,
    long? ContextWindowSize,
    string? Cwd);

/// <summary>
/// Non-fatal parse diagnostic recorded while reading Antigravity transcript or statusline JSONL files.
/// </summary>
/// <param name="LineNumber">1-based line number within the source file.</param>
/// <param name="Code">Diagnostic code (<see cref="AntigravityTranscriptMapper.MalformedJsonCode"/>).</param>
/// <param name="Message">Sanitized diagnostic description containing no raw JSON payload.</param>
public record AntigravityParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> for Antigravity conversation transcripts
/// (<c>brain/&lt;conversationId&gt;/.system_generated/logs/transcript.jsonl</c>) and
/// <c>antigravity-cli</c> statusline snapshots (<c>cache/antigravity-statusline.jsonl</c>).
/// </summary>
public partial class AntigravityTranscriptMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for Antigravity.</summary>
    public const string AntigravityHarnessId = "antigravity";

    /// <summary>Canonical harness identifier for Antigravity CLI.</summary>
    public const string AntigravityCliHarnessId = "antigravity-cli";

    /// <summary>Canonical harness identifier for Antigravity IDE.</summary>
    public const string AntigravityIdeHarnessId = "antigravity-ide";

    /// <summary>Source format identifier for Antigravity transcript JSONL units.</summary>
    public const string AntigravityJsonlFormat = "antigravity-jsonl";

    /// <summary>Source format identifier for Antigravity binary protobuf/SQLite units.</summary>
    public const string AntigravityPbFormat = "antigravity-pb";

    /// <summary>Source format identifier for Antigravity CLI statusline JSONL cache units.</summary>
    public const string AntigravityStatusLineFormat = "antigravity-statusline-jsonl";

    /// <summary>Diagnostic code emitted when a JSONL line cannot be parsed as a JSON object.</summary>
    public const string MalformedJsonCode = "ANTIGRAVITY_MALFORMED_LINE";

    private const string AgyStepPrefix = "agy-step:";
    private const string SkillFileName = "SKILL.md";

    private static readonly string[] FileArgNames =
    [
        "AbsolutePath",
        "TargetFile",
        "SearchPath",
        "DirectoryPath",
        "Path",
        "File",
    ];

    private static readonly JsonWriterOptions CanonicalWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc />
    public string HarnessId => AntigravityHarnessId;

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        string sourceHarnessId = string.IsNullOrWhiteSpace(unit.Unit.HarnessId)
            ? AntigravityHarnessId
            : unit.Unit.HarnessId.Trim();
        CollectedSource source = new(sourceHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);

        if (string.Equals(unit.Unit.Format, AntigravityPbFormat, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        if (IsStatusLineUnit(unit.Unit))
        {
            string statusHarnessId = ResolveStatusLineHarnessId(unit.Unit);
            CollectedSource statusSource = new(statusHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);
            return MapStatusLine(unit, statusSource, statusHarnessId, importedAtUtc);
        }

        string transcriptHarnessId = ResolveTranscriptHarnessId(unit.Unit);
        CollectedSource transcriptSource = new(transcriptHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);
        return MapTranscript(unit, transcriptSource, transcriptHarnessId, importedAtUtc);
    }

    /// <summary>
    /// Formats the pairing identifier (<c>agy-step:&lt;lastStepIndex&gt;</c>) shared between
    /// Antigravity conversation metadata rows and transcript segmentation.
    /// </summary>
    /// <param name="lastStepIndex">Zero-based <c>last_step_index</c> value.</param>
    /// <returns>Formatted pairing identifier string.</returns>
    public static string AgyStepId(long lastStepIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"{AgyStepPrefix}{lastStepIndex}");

    /// <summary>
    /// Extracts the <c>last_step_index</c> encoded in <paramref name="pairingId"/>, or
    /// <see langword="null"/> for any other string.
    /// </summary>
    /// <param name="pairingId">Candidate pairing identifier.</param>
    /// <returns>Parsed non-negative step index, or <see langword="null"/>.</returns>
    public static long? TryParseAgyStepIndex(string? pairingId)
    {
        if (string.IsNullOrEmpty(pairingId) || !pairingId.StartsWith(AgyStepPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string digits = pairingId[AgyStepPrefix.Length..];
        if (digits.Length == 0)
        {
            return null;
        }

        foreach (char ch in digits)
        {
            if (ch is < '0' or > '9')
            {
                return null;
            }
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;
    }

    /// <summary>
    /// Infers the skill name from a <c>SKILL.md</c> file path (its parent directory name),
    /// or returns <see langword="null"/> for any non-skill path or invalid directory segment.
    /// </summary>
    /// <param name="path">Candidate file path passed to <c>view_file</c>.</param>
    /// <returns>Skill directory name when valid; otherwise <see langword="null"/>.</returns>
    public static string? SkillNameFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/');
        int lastSlash = normalized.LastIndexOf('/');
        string fileName = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
        if (!string.Equals(fileName, SkillFileName, StringComparison.Ordinal))
        {
            return null;
        }

        if (lastSlash <= 0)
        {
            return null;
        }

        string dirPath = normalized[..lastSlash].TrimEnd('/');
        if (dirPath.Length == 0)
        {
            return null;
        }

        int prevSlash = dirPath.LastIndexOf('/');
        string dirName = prevSlash >= 0 ? dirPath[(prevSlash + 1)..] : dirPath;
        if (dirName is "." or ".." || !SkillNameRegex().IsMatch(dirName))
        {
            return null;
        }

        return dirName;
    }

    /// <summary>
    /// Parses all valid transcript steps (<c>USER_INPUT</c>, <c>PLANNER_RESPONSE</c>, and <c>GENERIC</c>)
    /// from <paramref name="rawText"/>.
    /// </summary>
    /// <param name="rawText">Raw <c>transcript.jsonl</c> contents.</param>
    /// <returns>Ordered list of parsed transcript steps.</returns>
    public static IReadOnlyList<AntigravityTranscriptStep> ParseTranscriptSteps(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        return ParseTranscriptStepsCore(rawText, problems: null);
    }

    /// <summary>
    /// Splits <paramref name="steps"/> into segments matching ascending <paramref name="lastStepIndexes"/>,
    /// where call <c>N</c> owns steps with <c>step_index</c> in <c>(lsi[N], lsi[N+1]]</c>.
    /// </summary>
    /// <param name="steps">Parsed transcript steps.</param>
    /// <param name="lastStepIndexes">Ascending distinct <c>last_step_index</c> values.</param>
    /// <returns>One <see cref="AntigravityTranscriptSegment"/> per entry in <paramref name="lastStepIndexes"/>.</returns>
    public static IReadOnlyList<AntigravityTranscriptSegment> SegmentTranscript(
        IReadOnlyList<AntigravityTranscriptStep> steps,
        IReadOnlyList<long> lastStepIndexes)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(lastStepIndexes);

        List<AntigravityTranscriptStep> toolSteps = [];
        List<AntigravityTranscriptStep> resultSteps = [];
        foreach (AntigravityTranscriptStep step in steps)
        {
            if (string.Equals(step.StepType, "PLANNER_RESPONSE", StringComparison.Ordinal) && step.ToolCalls.Count > 0)
            {
                toolSteps.Add(step);
            }
            else if (string.Equals(step.StepType, "GENERIC", StringComparison.Ordinal) && step.Content is not null)
            {
                resultSteps.Add(step);
            }
        }

        List<AntigravityTranscriptSegment> segments = new(lastStepIndexes.Count);
        for (int i = 0; i < lastStepIndexes.Count; i++)
        {
            long from = lastStepIndexes[i];
            long? to = i + 1 < lastStepIndexes.Count ? lastStepIndexes[i + 1] : null;

            bool Owns(long index) => index > from && (!to.HasValue || index <= to.Value);

            List<AntigravityTranscriptStep> ownedSteps = [];
            int totalCalls = 0;
            foreach (AntigravityTranscriptStep step in toolSteps)
            {
                if (Owns(step.StepIndex))
                {
                    ownedSteps.Add(step);
                    totalCalls += step.ToolCalls.Count;
                }
            }

            string? output = null;
            if (totalCalls == 1)
            {
                foreach (AntigravityTranscriptStep resultStep in resultSteps)
                {
                    if (Owns(resultStep.StepIndex))
                    {
                        output = resultStep.Content;
                        break;
                    }
                }
            }

            bool hasAtOrBefore = false;
            bool hasAfter = false;
            foreach (AntigravityTranscriptStep step in toolSteps)
            {
                if (step.StepIndex <= from + 1)
                {
                    hasAtOrBefore = true;
                }

                if (step.StepIndex > from)
                {
                    hasAfter = true;
                }

                if (hasAtOrBefore && hasAfter)
                {
                    break;
                }
            }

            segments.Add(new AntigravityTranscriptSegment(ownedSteps, hasAtOrBefore && hasAfter, output));
        }

        return segments;
    }

    private static CollectedBatch MapTranscript(
        NativeSourceUnit unit,
        CollectedSource source,
        string harnessId,
        DateTimeOffset importedAtUtc)
    {
        List<AntigravityParseProblem> parseProblems = [];
        List<AntigravityTranscriptStep> steps = ParseTranscriptStepsCore(unit.RawText ?? string.Empty, parseProblems);

        string sessionId = ResolveTranscriptSessionId(unit.Unit.SourceKey);
        List<CollectedProblem> problems = BuildProblems(parseProblems, unit.Unit.SourceKey, sessionId, harnessId, importedAtUtc);

        bool hasPlannerSteps = false;
        foreach (AntigravityTranscriptStep step in steps)
        {
            if (string.Equals(step.StepType, "PLANNER_RESPONSE", StringComparison.Ordinal))
            {
                hasPlannerSteps = true;
                break;
            }
        }

        if (!hasPlannerSteps)
        {
            return new CollectedBatch(source, [], [], problems);
        }

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: sessionId,
                HarnessId: harnessId,
                NativeSessionId: sessionId,
                Workspace: null,
                Title: null,
                ReportedUsage: []),
        ];

        string traceId = $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}";
        DateTimeOffset fallbackTimestamp = ResolveTranscriptFallbackTimestamp(steps, unit.LastWriteUtc, importedAtUtc);

        List<CollectedRecord> records = [];
        HashSet<string> usedRecordIds = new(StringComparer.Ordinal);
        List<string> pendingUserMessages = [];

        for (int i = 0; i < steps.Count; i++)
        {
            AntigravityTranscriptStep step = steps[i];

            if (string.Equals(step.StepType, "USER_INPUT", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(step.Content))
                {
                    pendingUserMessages.Add(step.Content);
                }

                continue;
            }

            if (!string.Equals(step.StepType, "PLANNER_RESPONSE", StringComparison.Ordinal))
            {
                continue;
            }

            string parentId = EnsureUniqueRecordId(
                usedRecordIds,
                string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}:step-{step.StepIndex}"));

            DateTimeOffset startedAt = ParseTimestampOrDefault(step.CreatedAt, fallbackTimestamp);
            RecordStatus status = MapStepStatus(step.Status);

            List<CollectedContentPart> parts = [];
            int position = 0;
            string? correctionRule = null;

            foreach (string userMessage in pendingUserMessages)
            {
                if (correctionRule is null)
                {
                    CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(userMessage);
                    if (check.Matched)
                    {
                        correctionRule = check.Rule;
                    }
                }

                parts.Add(MeasurePart(position++, ContentBucket.UserMessage, userMessage, "user"));
            }

            pendingUserMessages.Clear();

            if (!string.IsNullOrEmpty(step.Content))
            {
                parts.Add(MeasurePart(position++, ContentBucket.ConversationHistory, step.Content, "assistant"));
            }

            List<string> skillsInvoked = CollectDistinctSkills(step.ToolCalls);
            string? errorMessage = status == RecordStatus.Error
                ? string.Create(CultureInfo.InvariantCulture, $"Antigravity planner step {step.StepIndex} ended with error.")
                : null;

            CollectedRecord parentRecord = new(
                RecordId: parentId,
                HarnessId: harnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Internal,
                Name: $"{harnessId}:llm.invoke",
                StartedAt: startedAt,
                Status: status,
                SessionKey: sessionId,
                ParentRecordId: null,
                TraceId: traceId,
                Model: null,
                EndedAt: null,
                ReportedDurationMs: null,
                Tokens: [],
                ReportedCost: null,
                Parts: parts,
                Tool: null,
                Agent: null,
                SkillsInvoked: skillsInvoked,
                AgentInstanceId: null,
                ToolDefinitions: [],
                Signals: new CollectedRecordSignals(errorMessage, null, correctionRule, null));

            records.Add(parentRecord);

            if (step.ToolCalls.Count == 0)
            {
                continue;
            }

            AntigravityTranscriptStep? pairedGeneric = step.ToolCalls.Count == 1
                ? FindNextGenericStep(steps, i + 1)
                : null;

            for (int t = 0; t < step.ToolCalls.Count; t++)
            {
                AntigravityTranscriptToolCall toolCall = step.ToolCalls[t];
                CollectedRecord child = BuildToolInvokeRecord(
                    parentRecord,
                    harnessId,
                    sessionId,
                    traceId,
                    toolCall,
                    pairedGeneric,
                    usedRecordIds);
                records.Add(child);
            }
        }

        return new CollectedBatch(source, sessions, records, problems);
    }

    private static CollectedBatch MapStatusLine(
        NativeSourceUnit unit,
        CollectedSource source,
        string harnessId,
        DateTimeOffset importedAtUtc)
    {
        List<AntigravityParseProblem> parseProblems = [];
        List<AntigravityStatusLineEntry> entries = ParseStatusLineEntries(unit.RawText ?? string.Empty, parseProblems);

        List<CollectedProblem> problems = BuildProblems(parseProblems, unit.Unit.SourceKey, sessionId: null, harnessId, importedAtUtc);

        Dictionary<string, List<StatusLineRun>> runsByConversation = new(StringComparer.Ordinal);
        List<string> conversationOrder = [];

        foreach (AntigravityStatusLineEntry entry in entries)
        {
            string signature = BuildUsageSignature(entry);
            if (!runsByConversation.TryGetValue(entry.ConversationId, out List<StatusLineRun>? runs))
            {
                runs = [];
                runsByConversation[entry.ConversationId] = runs;
                conversationOrder.Add(entry.ConversationId);
            }

            if (runs.Count > 0 && string.Equals(runs[^1].Signature, signature, StringComparison.Ordinal))
            {
                runs[^1].Count++;
                runs[^1].Entry = entry;
            }
            else
            {
                runs.Add(new StatusLineRun(entry, signature));
            }
        }

        DateTimeOffset fallbackTimestamp = unit.LastWriteUtc != default
            ? unit.LastWriteUtc.ToUniversalTime()
            : importedAtUtc.ToUniversalTime();

        List<CollectedSession> sessions = [];
        List<CollectedRecord> records = [];
        HashSet<string> usedRecordIds = new(StringComparer.Ordinal);

        foreach (string conversationId in conversationOrder)
        {
            List<StatusLineRun> runs = runsByConversation[conversationId];
            int turnIndex = 0;
            AntigravityStatusLineEntry? previousSnapshot = null;
            bool emittedForConversation = false;

            for (int i = 0; i < runs.Count; i++)
            {
                StatusLineRun run = runs[i];
                bool isLastRun = i == runs.Count - 1;
                if (run.Count == 1 && !isLastRun)
                {
                    continue;
                }

                AntigravityStatusLineEntry current = run.Entry;
                (long input, long output, long cacheWrite, long cacheRead) = previousSnapshot is not null
                    && IsMonotonicUsage(current, previousSnapshot)
                    ? (
                        current.InputTokens - previousSnapshot.InputTokens,
                        current.OutputTokens - previousSnapshot.OutputTokens,
                        current.CacheCreationInputTokens - previousSnapshot.CacheCreationInputTokens,
                        current.CacheReadInputTokens - previousSnapshot.CacheReadInputTokens)
                    : (
                        current.InputTokens,
                        current.OutputTokens,
                        current.CacheCreationInputTokens,
                        current.CacheReadInputTokens);

                previousSnapshot = current;

                if (input == 0 && output == 0 && cacheWrite == 0 && cacheRead == 0)
                {
                    continue;
                }

                string candidateId = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{CanonSynth.SynthSpanPrefix}antigravity-statusline:{conversationId}:{turnIndex}:{run.Signature}");
                turnIndex++;

                string recordId = EnsureUniqueRecordId(usedRecordIds, candidateId);
                DateTimeOffset startedAt = ParseTimestampOrDefault(current.Timestamp, fallbackTimestamp);

                List<CollectedTokenCount> tokens =
                [
                    new CollectedTokenCount(TokenClass.Input, input),
                    new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
                    new CollectedTokenCount(TokenClass.CacheWrite, cacheWrite),
                    new CollectedTokenCount(TokenClass.Output, output),
                ];

                records.Add(new CollectedRecord(
                    RecordId: recordId,
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Internal,
                    Name: $"{harnessId}:{current.Model}",
                    StartedAt: startedAt,
                    Status: RecordStatus.Ok,
                    SessionKey: conversationId,
                    ParentRecordId: null,
                    TraceId: $"{CanonSynth.SynthSpanPrefix}{harnessId}:{conversationId}",
                    Model: current.Model,
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: tokens,
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null),
                    ReportedInputTokens: input + cacheRead + cacheWrite,
                    ReportedOutputTokens: output));

                emittedForConversation = true;
            }

            if (emittedForConversation)
            {
                sessions.Add(new CollectedSession(
                    SessionKey: conversationId,
                    HarnessId: harnessId,
                    NativeSessionId: conversationId,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []));
            }
        }

        return new CollectedBatch(source, sessions, records, problems);
    }

    private static List<AntigravityTranscriptStep> ParseTranscriptStepsCore(
        string rawText,
        List<AntigravityParseProblem>? problems)
    {
        List<AntigravityTranscriptStep> steps = [];
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

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                problems?.Add(new AntigravityParseProblem(
                    lineNumber,
                    MalformedJsonCode,
                    string.Create(CultureInfo.InvariantCulture, $"Skipped malformed Antigravity JSONL line {lineNumber}.")));
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems?.Add(new AntigravityParseProblem(
                        lineNumber,
                        MalformedJsonCode,
                        string.Create(CultureInfo.InvariantCulture, $"Skipped non-object Antigravity JSONL line {lineNumber}.")));
                    continue;
                }

                JsonElement root = doc.RootElement;
                if (!TryReadSafeInteger(root, "step_index", out long stepIndex))
                {
                    continue;
                }

                string? stepType = ReadOptionalString(root, "type");
                if (string.IsNullOrEmpty(stepType))
                {
                    continue;
                }

                string? sourceLabel = SafeOptionalText(ReadOptionalString(root, "source"));
                string? status = SafeOptionalText(ReadOptionalString(root, "status"));
                string? createdAt = SafeOptionalText(ReadOptionalString(root, "created_at"));
                string? content = ReadOptionalString(root, "content");

                if (string.Equals(stepType, "USER_INPUT", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        steps.Add(new AntigravityTranscriptStep(stepIndex, stepType, sourceLabel, status, createdAt, content, []));
                    }

                    continue;
                }

                if (string.Equals(stepType, "GENERIC", StringComparison.Ordinal))
                {
                    if (content is not null)
                    {
                        steps.Add(new AntigravityTranscriptStep(stepIndex, stepType, sourceLabel, status, createdAt, content, []));
                    }

                    continue;
                }

                if (!string.Equals(stepType, "PLANNER_RESPONSE", StringComparison.Ordinal))
                {
                    continue;
                }

                List<AntigravityTranscriptToolCall> toolCalls = ParsePlannerToolCalls(root, stepIndex);
                if (toolCalls.Count == 0 && string.IsNullOrEmpty(content))
                {
                    continue;
                }

                steps.Add(new AntigravityTranscriptStep(stepIndex, stepType, sourceLabel, status, createdAt, content, toolCalls));
            }
        }

        return steps;
    }

    private static List<AntigravityTranscriptToolCall> ParsePlannerToolCalls(JsonElement root, long stepIndex)
    {
        if (!root.TryGetProperty("tool_calls", out JsonElement toolCallsEl) || toolCallsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<AntigravityTranscriptToolCall> calls = [];
        int toolIndex = 0;

        foreach (JsonElement rawCall in toolCallsEl.EnumerateArray())
        {
            if (rawCall.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? rawName = SafeOptionalText(ReadOptionalString(rawCall, "name"));
            if (rawName is null)
            {
                continue;
            }

            string? explicitCallId = SafeOptionalText(ReadOptionalString(rawCall, "call_id"))
                ?? SafeOptionalText(ReadOptionalString(rawCall, "id"));
            string callId = explicitCallId ?? string.Create(CultureInfo.InvariantCulture, $"step-{stepIndex}-call-{toolIndex}");
            toolIndex++;

            JsonElement argsEl = rawCall.TryGetProperty("args", out JsonElement candidateArgs) && candidateArgs.ValueKind == JsonValueKind.Object
                ? candidateArgs
                : default;

            string rawArgsJson = argsEl.ValueKind == JsonValueKind.Object
                ? SerializeDecodedArgsObject(argsEl)
                : "{}";

            string? fileArg = argsEl.ValueKind == JsonValueKind.Object
                ? ReadDecodedStringArg(argsEl, FileArgNames)
                : null;
            string? skillName = string.Equals(rawName, "view_file", StringComparison.Ordinal)
                ? SkillNameFromPath(fileArg)
                : null;

            calls.Add(new AntigravityTranscriptToolCall(callId, rawName, rawArgsJson, skillName));
        }

        return calls;
    }

    private static List<AntigravityStatusLineEntry> ParseStatusLineEntries(
        string rawText,
        List<AntigravityParseProblem> problems)
    {
        List<AntigravityStatusLineEntry> entries = [];
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

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                problems.Add(new AntigravityParseProblem(
                    lineNumber,
                    MalformedJsonCode,
                    string.Create(CultureInfo.InvariantCulture, $"Skipped malformed Antigravity statusline JSONL line {lineNumber}.")));
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new AntigravityParseProblem(
                        lineNumber,
                        MalformedJsonCode,
                        string.Create(CultureInfo.InvariantCulture, $"Skipped non-object Antigravity statusline JSONL line {lineNumber}.")));
                    continue;
                }

                AntigravityStatusLineEntry? parsed = TryParseStatusLineObject(doc.RootElement);
                if (parsed is not null)
                {
                    entries.Add(parsed);
                }
            }
        }

        return entries;
    }

    private static AntigravityStatusLineEntry? TryParseStatusLineObject(JsonElement root)
    {
        string? conversationId = SafeOptionalText(ReadOptionalString(root, "conversation_id"))
            ?? SafeOptionalText(ReadOptionalString(root, "conversationId"));
        if (conversationId is null)
        {
            return null;
        }

        JsonElement usageEl = default;
        if (root.TryGetProperty("context_window", out JsonElement contextWindowEl)
            && contextWindowEl.ValueKind == JsonValueKind.Object
            && contextWindowEl.TryGetProperty("current_usage", out JsonElement currentUsageEl)
            && currentUsageEl.ValueKind == JsonValueKind.Object)
        {
            usageEl = currentUsageEl;
        }
        else if (root.TryGetProperty("usage", out JsonElement legacyUsageEl) && legacyUsageEl.ValueKind == JsonValueKind.Object)
        {
            usageEl = legacyUsageEl;
        }

        if (usageEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long inputTokens = ReadPositiveInteger(usageEl, "input_tokens", "inputTokens");
        long outputTokens = ReadPositiveInteger(usageEl, "output_tokens", "outputTokens");
        long cacheCreationTokens = ReadPositiveInteger(usageEl, "cache_creation_input_tokens", "cacheCreationInputTokens");
        long cacheReadTokens = ReadPositiveInteger(usageEl, "cache_read_input_tokens", "cacheReadInputTokens");

        if (inputTokens == 0 && outputTokens == 0 && cacheCreationTokens == 0 && cacheReadTokens == 0)
        {
            return null;
        }

        string? model = null;
        string? modelDisplayName = null;
        if (root.TryGetProperty("model", out JsonElement modelEl))
        {
            if (modelEl.ValueKind == JsonValueKind.String)
            {
                model = SafeOptionalText(modelEl.GetString());
            }
            else if (modelEl.ValueKind == JsonValueKind.Object)
            {
                modelDisplayName = SafeOptionalText(ReadOptionalString(modelEl, "display_name"));
                model = SafeOptionalText(ReadOptionalString(modelEl, "id")) ?? modelDisplayName;
            }
        }

        if (model is null || string.Equals(model, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? timestamp = SafeOptionalText(ReadOptionalString(root, "at"))
            ?? SafeOptionalText(ReadOptionalString(root, "timestamp"));
        string? sessionId = SafeOptionalText(ReadOptionalString(root, "session_id"))
            ?? SafeOptionalText(ReadOptionalString(root, "sessionId"));
        long? contextWindowSize = contextWindowEl.ValueKind == JsonValueKind.Object
            && TryReadSafeInteger(contextWindowEl, "context_window_size", out long cwSize)
            && cwSize > 0
            ? cwSize
            : null;
        string? cwd = SafeOptionalText(ReadOptionalString(root, "cwd"));

        return new AntigravityStatusLineEntry(
            Timestamp: timestamp,
            ConversationId: conversationId,
            SessionId: sessionId,
            Model: model,
            ModelDisplayName: modelDisplayName,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CacheCreationInputTokens: cacheCreationTokens,
            CacheReadInputTokens: cacheReadTokens,
            ContextWindowSize: contextWindowSize,
            Cwd: cwd);
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parentRecord,
        string harnessId,
        string sessionId,
        string traceId,
        AntigravityTranscriptToolCall toolCall,
        AntigravityTranscriptStep? pairedGeneric,
        HashSet<string> usedRecordIds)
    {
        byte[] hashBytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{toolCall.CallId}:{toolCall.ToolName}:{toolCall.RawArgumentsJson}"));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];
        string childId = EnsureUniqueRecordId(usedRecordIds, $"{parentRecord.RecordId}-t{toolHash}");

        ToolContentMeasurement inputMeasurement = ToolContentMeasurer.Measure(
            toolCall.RawArgumentsJson,
            CanonSynth.MaxToolArgumentsBytes);
        CollectedToolContent inputContent = inputMeasurement.ToToolContent(includeFingerprint: true);

        CollectedToolContent? resultContent = null;
        List<CollectedContentPart> parts = [];
        DateTimeOffset? endedAt = null;
        long? durationMs = null;

        if (pairedGeneric?.Content is not null)
        {
            ToolContentMeasurement resultMeasurement = ToolContentMeasurer.Measure(
                pairedGeneric.Content,
                CanonSynth.MaxToolResultBytes);
            resultContent = resultMeasurement.ToToolContent(includeFingerprint: false);

            parts.Add(new CollectedContentPart(
                Position: 0,
                Bucket: ContentBucket.ToolResultContent,
                PlainText: resultMeasurement.IsDocument ? null : resultMeasurement.PlainText,
                Withheld: resultMeasurement.IsDocument ? resultMeasurement.ToWithheldText() : null,
                ReportedTokens: null,
                Server: null,
                Role: "tool",
                Resident: null,
                OriginalUtf8BytesWhenTruncated: resultMeasurement.IsDocument
                    ? null
                    : resultMeasurement.OriginalUtf8BytesWhenTruncated));

            if (TryParseTimestamp(pairedGeneric.CreatedAt, out DateTimeOffset parsedEnd))
            {
                long diffMs = (long)Math.Round((parsedEnd - parentRecord.StartedAt).TotalMilliseconds);
                if (diffMs >= 0)
                {
                    endedAt = parsedEnd;
                    durationMs = diffMs;
                }
            }
        }

        RecordStatus status = pairedGeneric is null
            ? RecordStatus.Unset
            : MapStepStatus(pairedGeneric.Status) == RecordStatus.Error
                ? RecordStatus.Error
                : RecordStatus.Ok;
        bool? succeeded = pairedGeneric is null ? null : status != RecordStatus.Error;
        string? errorMessage = status == RecordStatus.Error
            ? SafeErrorMessage(pairedGeneric?.Content)
            : null;
        CollectedError? error = errorMessage is not null ? new CollectedError(errorMessage, null) : null;

        (string? mcpServerName, string? mcpToolName) = ParseMcpToolNames(toolCall.ToolName);

        CollectedToolCall collectedTool = new(
            CallId: toolCall.CallId,
            ToolName: toolCall.ToolName,
            McpServerName: mcpServerName,
            McpToolName: mcpToolName,
            Input: inputContent,
            Result: resultContent,
            Succeeded: succeeded,
            Error: error,
            ExitCode: null);

        return new CollectedRecord(
            RecordId: childId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: toolCall.ToolName,
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
            Signals: new CollectedRecordSignals(errorMessage, null, null, null));
    }

    private static AntigravityTranscriptStep? FindNextGenericStep(
        IReadOnlyList<AntigravityTranscriptStep> steps,
        int startIndex)
    {
        for (int i = startIndex; i < steps.Count; i++)
        {
            AntigravityTranscriptStep candidate = steps[i];
            if (string.Equals(candidate.StepType, "PLANNER_RESPONSE", StringComparison.Ordinal)
                || string.Equals(candidate.StepType, "USER_INPUT", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(candidate.StepType, "GENERIC", StringComparison.Ordinal) && candidate.Content is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<string> CollectDistinctSkills(IReadOnlyList<AntigravityTranscriptToolCall> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return [];
        }

        List<string> skills = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (AntigravityTranscriptToolCall call in toolCalls)
        {
            if (call.InferredSkillName is not null && seen.Add(call.InferredSkillName))
            {
                skills.Add(call.InferredSkillName);
            }
        }

        return skills;
    }

    private static string SerializeDecodedArgsObject(JsonElement argsObj)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, CanonicalWriterOptions))
        {
            writer.WriteStartObject();
            foreach (JsonProperty prop in argsObj.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(prop.Name);
                WriteDecodedArgValue(writer, prop.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteDecodedArgValue(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            value.WriteTo(writer);
            return;
        }

        string? raw = value.GetString();
        if (raw is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (!TryParseJsonValue(raw, out JsonDocument? first))
        {
            writer.WriteStringValue(raw);
            return;
        }

        using (first)
        {
            if (first.RootElement.ValueKind != JsonValueKind.String)
            {
                first.RootElement.WriteTo(writer);
                return;
            }

            string? innerRaw = first.RootElement.GetString();
            if (innerRaw is null)
            {
                writer.WriteNullValue();
                return;
            }

            if (TryParseJsonValue(innerRaw, out JsonDocument? second))
            {
                using (second)
                {
                    second.RootElement.WriteTo(writer);
                    return;
                }
            }

            writer.WriteStringValue(innerRaw);
        }
    }

    private static string? ReadDecodedStringArg(JsonElement argsObj, string[] candidateNames)
    {
        foreach (string name in candidateNames)
        {
            if (!argsObj.TryGetProperty(name, out JsonElement valueEl))
            {
                continue;
            }

            string? decoded = DecodeArgString(valueEl);
            if (!string.IsNullOrEmpty(decoded))
            {
                return decoded;
            }
        }

        return null;
    }

    private static string? DecodeArgString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? raw = value.GetString();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!TryParseJsonValue(raw, out JsonDocument? first))
        {
            return raw;
        }

        using (first)
        {
            if (first.RootElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? decoded = first.RootElement.GetString();
            if (string.IsNullOrEmpty(decoded))
            {
                return null;
            }

            if (TryParseJsonValue(decoded, out JsonDocument? second))
            {
                using (second)
                {
                    return second.RootElement.ValueKind == JsonValueKind.String
                        ? second.RootElement.GetString()
                        : decoded;
                }
            }

            return decoded;
        }
    }

    private static bool TryParseJsonValue(string text, [NotNullWhen(true)] out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static CollectedContentPart MeasurePart(
        int position,
        ContentBucket bucket,
        string text,
        string role)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(text, CanonSynth.MaxToolResultBytes);
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
        IReadOnlyList<AntigravityParseProblem> parseProblems,
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

        foreach (AntigravityParseProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"antigravity-malformed:{sourceKey}:line-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Skipped malformed Antigravity JSONL line.";

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

    private static bool IsStatusLineUnit(SourceUnit unit)
    {
        if (string.Equals(unit.Format, AntigravityStatusLineFormat, StringComparison.Ordinal))
        {
            return true;
        }

        string normalized = unit.SourceKey.Replace('\\', '/');
        return normalized.EndsWith("/antigravity-statusline.jsonl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "antigravity-statusline.jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveStatusLineHarnessId(SourceUnit unit) =>
        string.Equals(unit.HarnessId, AntigravityCliHarnessId, StringComparison.Ordinal)
        || string.Equals(unit.HarnessId, AntigravityHarnessId, StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(unit.HarnessId)
            ? AntigravityCliHarnessId
            : unit.HarnessId.Trim();

    private static string ResolveTranscriptHarnessId(SourceUnit unit)
    {
        if (string.Equals(unit.HarnessId, AntigravityCliHarnessId, StringComparison.Ordinal)
            || string.Equals(unit.HarnessId, AntigravityIdeHarnessId, StringComparison.Ordinal)
            || string.Equals(unit.HarnessId, AntigravityHarnessId, StringComparison.Ordinal))
        {
            return unit.HarnessId;
        }

        string normalized = unit.SourceKey.Replace('\\', '/');
        if (normalized.Contains("/.gemini/antigravity-cli/", StringComparison.OrdinalIgnoreCase))
        {
            return AntigravityCliHarnessId;
        }

        if (normalized.Contains("/.gemini/antigravity-ide/", StringComparison.OrdinalIgnoreCase))
        {
            return AntigravityIdeHarnessId;
        }

        return string.IsNullOrWhiteSpace(unit.HarnessId) ? AntigravityHarnessId : unit.HarnessId.Trim();
    }

    private static string ResolveTranscriptSessionId(string sourceKey)
    {
        string normalized = sourceKey.Replace('\\', '/');
        const string brainMarker = "/brain/";
        int brainIndex = normalized.LastIndexOf(brainMarker, StringComparison.OrdinalIgnoreCase);
        if (brainIndex >= 0)
        {
            int start = brainIndex + brainMarker.Length;
            int end = normalized.IndexOf('/', start);
            if (end > start)
            {
                string? candidate = SafeOptionalText(normalized[start..end]);
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        string stem = Path.GetFileNameWithoutExtension(normalized);
        if (!string.IsNullOrWhiteSpace(stem)
            && !string.Equals(stem, "transcript", StringComparison.OrdinalIgnoreCase))
        {
            string? safeStem = SafeOptionalText(stem);
            if (safeStem is not null)
            {
                return safeStem;
            }
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return $"antigravity-{Convert.ToHexStringLower(digest)[..16]}";
    }

    private static string BuildUsageSignature(AntigravityStatusLineEntry entry) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Model}:{entry.InputTokens}:{entry.OutputTokens}:{entry.CacheCreationInputTokens}:{entry.CacheReadInputTokens}");

    private static bool IsMonotonicUsage(AntigravityStatusLineEntry current, AntigravityStatusLineEntry previous) =>
        current.InputTokens >= previous.InputTokens
        && current.OutputTokens >= previous.OutputTokens
        && current.CacheCreationInputTokens >= previous.CacheCreationInputTokens
        && current.CacheReadInputTokens >= previous.CacheReadInputTokens;

    private static DateTimeOffset ResolveTranscriptFallbackTimestamp(
        IReadOnlyList<AntigravityTranscriptStep> steps,
        DateTimeOffset sourceLastWriteUtc,
        DateTimeOffset importedAtUtc)
    {
        foreach (AntigravityTranscriptStep step in steps)
        {
            if (TryParseTimestamp(step.CreatedAt, out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        if (sourceLastWriteUtc != default)
        {
            return sourceLastWriteUtc.ToUniversalTime();
        }

        return importedAtUtc.ToUniversalTime();
    }

    private static DateTimeOffset ParseTimestampOrDefault(string? timestamp, DateTimeOffset fallback) =>
        TryParseTimestamp(timestamp, out DateTimeOffset parsed) ? parsed : fallback;

    private static bool TryParseTimestamp(string? timestamp, out DateTimeOffset parsed)
    {
        if (!string.IsNullOrWhiteSpace(timestamp)
            && DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset instant))
        {
            parsed = instant.ToUniversalTime();
            return true;
        }

        parsed = default;
        return false;
    }

    private static RecordStatus MapStepStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return RecordStatus.Unspecified;
        }

        if (string.Equals(status, "DONE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            return RecordStatus.Ok;
        }

        if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return RecordStatus.Error;
        }

        return RecordStatus.Unspecified;
    }

    private static (string? ServerName, string? ToolName) ParseMcpToolNames(string rawName)
    {
        if (!rawName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return (null, null);
        }

        string remainder = rawName[5..];
        int sep = remainder.IndexOf("__", StringComparison.Ordinal);
        if (sep <= 0 || sep + 2 >= remainder.Length)
        {
            return (null, null);
        }

        string? server = SafeOptionalText(remainder[..sep]);
        string? tool = SafeOptionalText(remainder[(sep + 2)..]);
        return server is not null && tool is not null ? (server, tool) : (null, null);
    }

    private static string EnsureUniqueRecordId(HashSet<string> usedRecordIds, string candidate)
    {
        if (usedRecordIds.Add(candidate))
        {
            return candidate;
        }

        int suffix = 2;
        while (true)
        {
            string indexed = string.Create(CultureInfo.InvariantCulture, $"{candidate}-{suffix}");
            if (usedRecordIds.Add(indexed))
            {
                return indexed;
            }

            suffix++;
        }
    }

    private static bool TryReadSafeInteger(JsonElement obj, string propertyName, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(propertyName, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return el.TryGetInt64(out value);
    }

    private static long ReadPositiveInteger(JsonElement obj, string primaryKey, string fallbackKey)
    {
        if (TryReadSafeInteger(obj, primaryKey, out long primary) && primary > 0)
        {
            return primary;
        }

        if (TryReadSafeInteger(obj, fallbackKey, out long fallback) && fallback > 0)
        {
            return fallback;
        }

        return 0L;
    }

    private static string? ReadOptionalString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return el.GetString();
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

    private static string SafeErrorMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || JsonDocumentText.IsDocument(content))
        {
            return "Tool execution failed.";
        }

        return content.Length > 512 ? content[..512] : content;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillNameRegex();

    private sealed class StatusLineRun(AntigravityStatusLineEntry entry, string signature)
    {
        public AntigravityStatusLineEntry Entry { get; set; } = entry;
        public string Signature { get; } = signature;
        public int Count { get; set; } = 1;
    }
}
