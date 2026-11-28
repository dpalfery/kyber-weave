using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Kilo;

/// <summary>
/// Parsed <c>kilo.db</c> message/part row export for one Kilo shared-runtime session.
/// </summary>
/// <remarks>
/// Opening SQLite files in unit tests is forbidden (OD-9), so the offline seam parses a JSON
/// export of the <c>message</c> and <c>part</c> rows the <c>WITH RECURSIVE session_tree</c> query in
/// <c>dash/src/synth/readers/kilo.ts</c> would return, rather than the database file itself.
/// </remarks>
/// <param name="SessionId">Root session identifier the export was lifted for, when present.</param>
/// <param name="Title">Session title from the <c>session</c> row, when present.</param>
/// <param name="Messages">Message rows in export order (re-sorted by <c>time_created, id</c> at map time).</param>
/// <param name="Parts">Part rows in export order, grouped by message at map time.</param>
/// <param name="ParseProblems">Non-fatal document-level parse diagnostics.</param>
public record KiloSharedRuntimeDocument(
    string? SessionId,
    string? Title,
    IReadOnlyList<KiloMessageRow> Messages,
    IReadOnlyList<KiloPartRow> Parts,
    IReadOnlyList<KiloParseProblem> ParseProblems);

/// <summary>
/// Typed representation of one exported <c>kilo.db</c> <c>message</c> row.
/// </summary>
/// <param name="Id">Native message identifier (<c>message.id</c>), the id-pairing key for turns.</param>
/// <param name="SessionId">Owning session identifier (<c>message.session_id</c>), possibly a child session.</param>
/// <param name="TimeCreatedMs">Row creation instant (<c>message.time_created</c>), when usable.</param>
/// <param name="Data">Parsed message payload (<c>message.data</c>).</param>
public record KiloMessageRow(
    string Id,
    string SessionId,
    long? TimeCreatedMs,
    KiloMessageData Data);

/// <summary>
/// Typed representation of one exported <c>kilo.db</c> <c>part</c> row.
/// </summary>
/// <param name="Id">Part row identifier (<c>part.id</c>).</param>
/// <param name="MessageId">Owning message identifier (<c>part.message_id</c>).</param>
/// <param name="TimeCreatedMs">Row creation instant (<c>part.time_created</c>), when usable.</param>
/// <param name="Data">Parsed part payload (<c>part.data</c>).</param>
public record KiloPartRow(
    string Id,
    string MessageId,
    long? TimeCreatedMs,
    KiloPartData Data);

/// <summary>
/// Token counters and attribution carried on one Kilo message payload, mirroring
/// <c>MessageData</c> in <c>dash/src/providers/session-message.ts</c>.
/// </summary>
/// <param name="Role">Message role (<c>role</c>): <c>user</c>, <c>assistant</c>, or <c>model</c>.</param>
/// <param name="ModelId">Raw model identifier (<c>modelID</c>), when present.</param>
/// <param name="ProviderId">Raw provider identifier (<c>providerID</c>) qualifying <paramref name="ModelId"/>, when present.</param>
/// <param name="ModelText">Resolved model string (<c>modelID</c>, <c>model</c> string, or <c>{id, providerID}</c> pair).</param>
/// <param name="Cost">Harness-reported cost (<c>cost</c>), when finite.</param>
/// <param name="InputTokens">Exclusive input tokens across the <c>tokens</c>, <c>usage</c>, and flat spellings.</param>
/// <param name="OutputTokens">Output tokens across the <c>tokens</c>, <c>usage</c>, and flat spellings.</param>
/// <param name="ReasoningTokens">Reasoning tokens across the <c>tokens</c> and flat spellings.</param>
/// <param name="CacheReadTokens">Cache-read tokens across the <c>tokens</c>, <c>usage</c>, and flat spellings.</param>
/// <param name="CacheWriteTokens">Cache-write tokens across the <c>tokens</c>, <c>usage</c>, and flat spellings.</param>
/// <param name="ParentId">Capture-store join key (<c>parentID</c>), the answered user message id.</param>
public record KiloMessageData(
    string? Role,
    string? ModelId,
    string? ProviderId,
    string ModelText,
    double? Cost,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    string? ParentId);

/// <summary>
/// Content or tool payload carried on one Kilo part, mirroring <c>PartData</c> in
/// <c>dash/src/providers/session-message.ts</c>.
/// </summary>
/// <param name="PartType">Part discriminator (<c>type</c>): <c>text</c>, <c>markdown</c>, <c>tool</c>, and others.</param>
/// <param name="Text">Text payload for <c>text</c>/<c>markdown</c> parts.</param>
/// <param name="Tool">Raw tool spelling for <c>tool</c> parts (<c>tool</c>, for example <c>bash</c>).</param>
/// <param name="CallId">Durable tool call identity (<c>callID</c>).</param>
/// <param name="InputJson">Raw <c>state.input</c> JSON for object/array inputs.</param>
/// <param name="OutputText">Recorded tool output (<c>state.output</c>, serialized; <c>state.error</c> fallback).</param>
/// <param name="ErrorText">Recorded error message (<c>state.error</c>), when a string.</param>
/// <param name="Status">Tool completion status (<c>state.status</c>, for example <c>completed</c> or <c>error</c>).</param>
/// <param name="StartTimeMs">Tool start instant (<c>state.time.start</c>), when finite.</param>
/// <param name="EndTimeMs">Tool end instant (<c>state.time.end</c>), when finite.</param>
/// <param name="SkillName">Invoked skill name (<c>state.input.name</c> on <c>skill</c> tool parts).</param>
/// <param name="SubagentType">Spawned subagent type (<c>state.input.subagent_type</c> on <c>task</c> tool parts).</param>
public record KiloPartData(
    string PartType,
    string? Text,
    string? Tool,
    string? CallId,
    string? InputJson,
    string OutputText,
    string? ErrorText,
    string? Status,
    long? StartTimeMs,
    long? EndTimeMs,
    string? SkillName,
    string? SubagentType);

/// <summary>
/// Parsed Kilo VS Code legacy <c>ui_messages.json</c> task tree, mirroring
/// <c>createClineParser</c> in <c>dash/src/providers/vscode-cline-parser.ts</c>.
/// </summary>
/// <param name="TaskId">Task directory name owning <c>ui_messages.json</c>.</param>
/// <param name="UserMessage">Leading user prompt text (first <c>user_feedback</c>/<c>text</c> entry, 500 chars).</param>
/// <param name="Model">Resolved model from companion history, or the shared cline-family fallback.</param>
/// <param name="Workspace">Workspace directory from companion history, when present.</param>
/// <param name="Requests">Usage-bearing <c>api_req_started</c> entries in file order.</param>
/// <param name="ParseProblems">Non-fatal document-level parse diagnostics.</param>
public record KiloLegacyDocument(
    string? TaskId,
    string? UserMessage,
    string Model,
    string? Workspace,
    IReadOnlyList<KiloLegacyRequest> Requests,
    IReadOnlyList<KiloParseProblem> ParseProblems);

/// <summary>
/// Usage counters carried on one legacy <c>api_req_started</c> entry.
/// </summary>
/// <param name="Index">Zero-based position among <c>api_req_started</c> entries, including skipped ones.</param>
/// <param name="TokensIn">Input tokens (<c>tokensIn</c>).</param>
/// <param name="TokensOut">Output tokens (<c>tokensOut</c>).</param>
/// <param name="CacheReads">Cache-read tokens (<c>cacheReads</c>).</param>
/// <param name="CacheWrites">Cache-write tokens (<c>cacheWrites</c>).</param>
/// <param name="Cost">Reported cost (<c>cost</c>), when finite.</param>
/// <param name="Timestamp">ISO-8601 UTC entry timestamp (<c>ts</c>), when usable.</param>
public record KiloLegacyRequest(
    int Index,
    long TokensIn,
    long TokensOut,
    long CacheReads,
    long CacheWrites,
    double? Cost,
    string? Timestamp);

/// <summary>
/// Non-fatal parse problem encountered while reading a Kilo native payload.
/// </summary>
/// <param name="LineNumber">1-based line or row number within the native payload (0 for document-level failures).</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record KiloParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting the Kilo readers into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// <para>
/// Handles both Kilo surfaces: <c>kilo-shared-runtime</c> (a JSON export of the <c>kilo.db</c>
/// <c>message</c>/<c>part</c> rows read by <c>dash/src/synth/readers/kilo.ts</c>) and
/// <c>kilo-vscode-legacy</c> (the legacy <c>tasks/&lt;id&gt;/ui_messages.json</c> tree read by
/// <c>createClineParser</c> in <c>dash/src/providers/vscode-cline-parser.ts</c>). The payload shape
/// selects the parser: a top-level array parses as <c>ui_messages.json</c>, a top-level object as a
/// row export. Units attributed to <c>kilo</c> or <c>kilo-code</c> normalize to
/// <c>kilo-shared-runtime</c> via <c>NormalizeHarnessName</c>.
/// </para>
/// <para>
/// Shared-runtime semantics: one <c>llm.invoke</c> record per token-bearing assistant/model message
/// ordered by <c>time_created, id</c> (SQLite NULLs sort first, hence null times sort earliest),
/// keyed on the Kilo message id; zero-token assistant messages (router replies) yield no record.
/// Pending user texts accumulate per session and are consumed by the next turn; the last tool
/// output stays the session's tool-result evidence until a newer one lands. Child-session messages
/// from the session tree are included under the root session. Tool parts without a <c>callID</c>
/// contribute output as content but emit no <c>tool.invoke</c> record. <c>reasoning</c>,
/// <c>step-start</c>, <c>step-finish</c>, <c>patch</c>, <c>file</c>, and <c>compaction</c> parts carry
/// no bucket. kilo.db stores neither the runtime system prompt nor tool-definition schemas and the
/// offline seam has no capture store, so those buckets stay absent. Session-row token rollups and
/// table-cost estimation are discovery/projection concerns with no offline input.
/// </para>
/// <para>
/// Legacy semantics: each <c>api_req_started</c> entry with nonzero input or output tokens emits one
/// <c>llm.invoke</c> record; the first user prompt attaches to the first record only; the model and
/// workspace resolve from the companion <c>api_conversation_history.json</c> text (via
/// <c>NativeSourceUnit.CompanionText</c>), falling back to the shared cline-family default. The
/// legacy tree has no per-message pairing id, so record identity keys on the filtered entry index.
/// </para>
/// </remarks>
public class KiloMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for the Kilo shared-runtime SQLite store.</summary>
    public const string KiloSharedRuntimeHarnessId = "kilo-shared-runtime";

    /// <summary>Canonical harness identifier for legacy Kilo VS Code task trees.</summary>
    public const string KiloVscodeLegacyHarnessId = "kilo-vscode-legacy";

    /// <summary>Problem code emitted when a Kilo native payload cannot be parsed.</summary>
    public const string MalformedLineCode = "KILO_MALFORMED_LINE";

    /// <summary>Shared cline-family model fallback mirroring <c>createClineParser</c>'s default.</summary>
    public const string LegacyFallbackModel = "cline-auto";

    /// <summary>Maximum user prompt characters retained per legacy task.</summary>
    internal const int MaxUserTextLength = 500;

    private const string UnknownModel = "unknown";
    private const long MillisThreshold = 1_000_000_000_000L;

    /// <inheritdoc />
    public string HarnessId => KiloSharedRuntimeHarnessId;

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        string harnessId = ResolveHarnessId(unit.Unit.HarnessId);
        CollectedSource source = new(harnessId, unit.Unit.Kind, unit.Unit.SourceKey);
        if (unit.RawText is null)
        {
            // A null payload is a source the reader could not give us content for, not an empty session: say so.
            List<CollectedProblem> nullProblems = BuildProblems(
                [Fail($"source '{unit.Unit.SourceKey}' carried no readable content")],
                unit.Unit.SourceKey,
                unit.Unit.SourceKey,
                harnessId,
                importedAtUtc);
            return new CollectedBatch(source, [], [], nullProblems);
        }

        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        return unit.RawText.TrimStart().StartsWith('[')
            ? MapLegacy(unit, source, importedAtUtc, harnessId)
            : MapSharedRuntime(unit, source, importedAtUtc, harnessId);
    }

    /// <summary>
    /// Parses a JSON export of <c>kilo.db</c> <c>message</c>/<c>part</c> rows into a static
    /// <see cref="KiloSharedRuntimeDocument"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the row export.</param>
    /// <param name="sourceKey">Optional path locator reserved for diagnostics.</param>
    /// <returns>The parsed row-export document.</returns>
    public static KiloSharedRuntimeDocument ParseSharedRuntime(string? rawText, string? sourceKey = null)
    {
        _ = sourceKey;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new KiloSharedRuntimeDocument(null, null, [], [], []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException ex)
        {
            return new KiloSharedRuntimeDocument(null, null, [], [], [Fail(ex.Message)]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new KiloSharedRuntimeDocument(null, null, [], [], [Fail("Expected a top-level JSON object of message/part rows.")]);
            }

            JsonElement root = document.RootElement;
            string? sessionId = ReadText(root, "session_id");
            string? title = ReadText(root, "title");

            List<KiloMessageRow> messages = [];
            if (root.TryGetProperty("messages", out JsonElement messageArray))
            {
                if (messageArray.ValueKind != JsonValueKind.Array)
                {
                    return new KiloSharedRuntimeDocument(sessionId, title, [], [], [Fail("Expected \"messages\" to be a JSON array of row objects.")]);
                }

                foreach (JsonElement entry in messageArray.EnumerateArray())
                {
                    KiloMessageRow? row = ParseMessageRow(entry, sessionId);
                    if (row is not null)
                    {
                        messages.Add(row);
                    }
                }
            }

            List<KiloPartRow> parts = [];
            if (root.TryGetProperty("parts", out JsonElement partArray))
            {
                if (partArray.ValueKind != JsonValueKind.Array)
                {
                    return new KiloSharedRuntimeDocument(sessionId, title, [], [], [Fail("Expected \"parts\" to be a JSON array of row objects.")]);
                }

                foreach (JsonElement entry in partArray.EnumerateArray())
                {
                    KiloPartRow? row = ParsePartRow(entry);
                    if (row is not null)
                    {
                        parts.Add(row);
                    }
                }
            }

            return new KiloSharedRuntimeDocument(sessionId, title, messages, parts, []);
        }
    }

    /// <summary>
    /// Parses a legacy <c>ui_messages.json</c> task file into a static <see cref="KiloLegacyDocument"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of <c>ui_messages.json</c>.</param>
    /// <param name="sourceKey">Optional path locator used for task-id derivation and diagnostics.</param>
    /// <param name="companionText">Optional <c>api_conversation_history.json</c> text for model/workspace metadata.</param>
    /// <returns>The parsed legacy document.</returns>
    public static KiloLegacyDocument ParseLegacyUiMessages(string? rawText, string? sourceKey = null, string? companionText = null)
    {
        string? taskId = ExtractTaskId(sourceKey);
        (string model, string? workspace) = ParseCompanionHistory(companionText);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new KiloLegacyDocument(taskId, null, model, workspace, [], []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException ex)
        {
            return new KiloLegacyDocument(taskId, null, model, workspace, [], [Fail(ex.Message)]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new KiloLegacyDocument(taskId, null, model, workspace, [], [Fail("Expected ui_messages.json to hold a top-level JSON array.")]);
            }

            string? userMessage = null;
            List<KiloLegacyRequest> requests = [];
            int apiReqIndex = 0;
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!string.Equals(ReadText(entry, "type"), "say", StringComparison.Ordinal))
                {
                    continue;
                }

                string? say = ReadText(entry, "say");
                if (userMessage is null
                    && (string.Equals(say, "user_feedback", StringComparison.Ordinal)
                        || string.Equals(say, "text", StringComparison.Ordinal)))
                {
                    string? text = ReadText(entry, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        userMessage = text.Length <= MaxUserTextLength ? text : text[..MaxUserTextLength];
                    }

                    continue;
                }

                if (!string.Equals(say, "api_req_started", StringComparison.Ordinal))
                {
                    continue;
                }

                KiloLegacyRequest? request = ParseLegacyRequest(entry, apiReqIndex++);
                if (request is not null)
                {
                    requests.Add(request);
                }
            }

            return new KiloLegacyDocument(taskId, userMessage, model, workspace, requests, []);
        }
    }

    private CollectedBatch MapSharedRuntime(
        NativeSourceUnit unit,
        CollectedSource source,
        DateTimeOffset importedAtUtc,
        string harnessId)
    {
        KiloSharedRuntimeDocument document = ParseSharedRuntime(unit.RawText, unit.Unit.SourceKey);
        string sessionId = SafeOptionalText(document.SessionId) ?? unit.Unit.SourceKey;
        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId, harnessId, importedAtUtc);
        if (document.Messages.Count == 0)
        {
            return new CollectedBatch(source, [], [], problems);
        }

        Dictionary<string, List<KiloPartRow>> partsByMessage = new(StringComparer.Ordinal);
        foreach (KiloPartRow part in document.Parts)
        {
            if (!partsByMessage.TryGetValue(part.MessageId, out List<KiloPartRow>? list))
            {
                list = [];
                partsByMessage[part.MessageId] = list;
            }

            list.Add(part);
        }

        List<KiloMessageRow> ordered = [.. document.Messages];
        ordered.Sort(static (left, right) =>
        {
            int order = (left.TimeCreatedMs ?? long.MinValue).CompareTo(right.TimeCreatedMs ?? long.MinValue);
            return order != 0 ? order : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
        });

        Dictionary<string, List<string>> pendingUserText = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> latestToolResult = new(StringComparer.Ordinal);
        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (KiloMessageRow message in ordered)
        {
            string role = message.Data.Role ?? string.Empty;
            if (string.Equals(role, "user", StringComparison.Ordinal))
            {
                if (partsByMessage.TryGetValue(message.Id, out List<KiloPartRow>? userParts))
                {
                    List<string> texts = CollectTextParts(userParts);
                    if (texts.Count > 0)
                    {
                        if (!pendingUserText.TryGetValue(message.SessionId, out List<string>? pending))
                        {
                            pending = [];
                            pendingUserText[message.SessionId] = pending;
                        }

                        pending.AddRange(texts);
                    }
                }

                continue;
            }

            if (!string.Equals(role, "assistant", StringComparison.Ordinal)
                && !string.Equals(role, "model", StringComparison.Ordinal))
            {
                continue;
            }

            if (TokenTotal(message.Data) <= 0)
            {
                continue;
            }

            records.AddRange(BuildTurnRecords(
                unit,
                message,
                partsByMessage.GetValueOrDefault(message.Id, []),
                pendingUserText,
                latestToolResult,
                sessionId,
                harnessId,
                importedAtUtc,
                seenRecordIds));
        }

        if (records.Count == 0)
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
                Title: SafeOptionalText(document.Title),
                ReportedUsage: []),
        ];

        return new CollectedBatch(source, sessions, records, problems);
    }

    private CollectedBatch MapLegacy(
        NativeSourceUnit unit,
        CollectedSource source,
        DateTimeOffset importedAtUtc,
        string harnessId)
    {
        KiloLegacyDocument document = ParseLegacyUiMessages(unit.RawText, unit.Unit.SourceKey, unit.CompanionText);
        string taskId = SafeOptionalText(document.TaskId) ?? unit.Unit.SourceKey;
        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, taskId, harnessId, importedAtUtc);
        if (document.Requests.Count == 0)
        {
            return new CollectedBatch(source, [], [], problems);
        }

        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{taskId}");
        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (KiloLegacyRequest request in document.Requests)
        {
            string recordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{taskId}:req-{request.Index.ToString(CultureInfo.InvariantCulture)}"));
            DateTimeOffset startedAt = ResolveLegacyTimestamp(request.Timestamp, unit.LastWriteUtc, importedAtUtc);

            List<CollectedContentPart> parts = [];
            if (request.Index == 0 && !string.IsNullOrWhiteSpace(document.UserMessage))
            {
                parts.Add(ToolContentMeasurer.BuildContentPart(0, ContentBucket.ConversationHistory, document.UserMessage, "user", document.Model));
            }

            List<CollectedTokenCount> tokens = [];
            if (request.TokensIn > 0)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.Input, request.TokensIn));
            }

            if (request.TokensOut > 0)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.Output, request.TokensOut));
            }

            if (request.CacheReads > 0)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, request.CacheReads));
            }

            if (request.CacheWrites > 0)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, request.CacheWrites));
            }

            CollectedCost? reportedCost = request.Cost is > 0 && double.IsFinite(request.Cost.Value)
                ? new CollectedCost(request.Cost.Value, "USD")
                : null;

            records.Add(new CollectedRecord(
                RecordId: recordId,
                HarnessId: harnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Internal,
                Name: string.Create(CultureInfo.InvariantCulture, $"kilo:{document.Model}"),
                StartedAt: startedAt,
                Status: RecordStatus.Ok,
                SessionKey: taskId,
                ParentRecordId: null,
                TraceId: traceId,
                Model: document.Model,
                EndedAt: null,
                ReportedDurationMs: null,
                Tokens: tokens,
                ReportedCost: reportedCost,
                Parts: parts,
                Tool: null,
                Agent: null,
                SkillsInvoked: [],
                AgentInstanceId: null,
                ToolDefinitions: [],
                Signals: new CollectedRecordSignals(null, null, null, null),
                ReportedInputTokens: request.TokensIn + request.CacheReads + request.CacheWrites,
                ReportedOutputTokens: request.TokensOut));
        }

        CollectedWorkspace? workspace = SafeOptionalText(document.Workspace) is string workingDirectory
            ? new CollectedWorkspace(WorkingDirectory: workingDirectory, Repository: null, Branch: null)
            : null;
        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: taskId,
                HarnessId: harnessId,
                NativeSessionId: taskId,
                Workspace: workspace,
                Title: null,
                ReportedUsage: []),
        ];

        return new CollectedBatch(source, sessions, records, problems);
    }

    private List<CollectedRecord> BuildTurnRecords(
        NativeSourceUnit unit,
        KiloMessageRow message,
        IReadOnlyList<KiloPartRow> ownParts,
        Dictionary<string, List<string>> pendingUserText,
        Dictionary<string, List<string>> latestToolResult,
        string sessionId,
        string harnessId,
        DateTimeOffset importedAtUtc,
        HashSet<string> seenRecordIds)
    {
        string recordId = EnsureUniqueRecordId(
            seenRecordIds,
            string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}:{message.Id}"));
        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionId}");
        DateTimeOffset startedAt = ResolveStartedAt(message.TimeCreatedMs, unit.LastWriteUtc, importedAtUtc);

        List<CollectedContentPart> parts = [];
        int position = 0;
        if (pendingUserText.TryGetValue(message.SessionId, out List<string>? pending) && pending.Count > 0)
        {
            foreach (string text in pending)
            {
                parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ConversationHistory, text, "user", message.Data.ModelText));
            }

            pending.Clear();
        }

        foreach (KiloPartRow row in ownParts)
        {
            string? text = PartText(row.Data);
            if (text is not null)
            {
                parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ConversationHistory, text, "assistant", message.Data.ModelText));
            }
        }

        List<string> ownToolResults = CollectToolResultTexts(ownParts);
        List<string> resultTexts = ownToolResults.Count > 0
            ? ownToolResults
            : latestToolResult.GetValueOrDefault(message.SessionId, []);
        if (ownToolResults.Count > 0)
        {
            latestToolResult[message.SessionId] = ownToolResults;
        }

        foreach (string resultText in resultTexts)
        {
            parts.Add(ToolContentMeasurer.BuildContentPart(position++, ContentBucket.ToolResultContent, resultText, "assistant", message.Data.ModelText));
        }

        List<CollectedTokenCount> tokens = [];
        if (message.Data.InputTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, message.Data.InputTokens));
        }

        if (message.Data.OutputTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, message.Data.OutputTokens));
        }

        if (message.Data.ReasoningTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Reasoning, message.Data.ReasoningTokens));
        }

        if (message.Data.CacheReadTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, message.Data.CacheReadTokens));
        }

        if (message.Data.CacheWriteTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, message.Data.CacheWriteTokens));
        }

        CollectedCost? reportedCost = message.Data.Cost is > 0 && double.IsFinite(message.Data.Cost.Value)
            ? new CollectedCost(message.Data.Cost.Value, "USD")
            : null;

        List<string> skillsInvoked = ownParts
            .Where(static p => string.Equals(p.Data.Tool, "skill", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(p.Data.SkillName))
            .Select(static p => p.Data.SkillName ?? throw new InvalidOperationException("A skill part must carry a non-blank skill name."))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        CollectedRecord parent = new(
            RecordId: recordId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: string.Create(CultureInfo.InvariantCulture, $"kilo:{message.Data.ModelText}"),
            StartedAt: startedAt,
            Status: RecordStatus.Ok,
            SessionKey: sessionId,
            ParentRecordId: null,
            TraceId: traceId,
            Model: message.Data.ModelText,
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
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: message.Data.InputTokens + message.Data.CacheReadTokens + message.Data.CacheWriteTokens,
            ReportedOutputTokens: message.Data.OutputTokens);

        List<CollectedRecord> records = [parent];
        foreach (KiloPartRow row in ownParts)
        {
            KiloPartData part = row.Data;
            if (!string.Equals(part.PartType, "tool", StringComparison.Ordinal)
                || part.CallId is not { } callId
                || string.IsNullOrWhiteSpace(callId))
            {
                continue;
            }

            CollectedRecord tool = BuildToolRecord(
                unit,
                row,
                message,
                parent,
                startedAt,
                harnessId,
                importedAtUtc,
                seenRecordIds);
            records.Add(tool);

            if (part.SubagentType is { } subagentType && !string.IsNullOrWhiteSpace(subagentType))
            {
                records.Add(BuildAgentRecord(tool, callId, subagentType, harnessId, seenRecordIds));
            }
        }

        return records;
    }

    private CollectedRecord BuildToolRecord(
        NativeSourceUnit unit,
        KiloPartRow row,
        KiloMessageRow message,
        CollectedRecord parent,
        DateTimeOffset parentStartedAt,
        string harnessId,
        DateTimeOffset importedAtUtc,
        HashSet<string> seenRecordIds)
    {
        KiloPartData part = row.Data;
        string callId = part.CallId ?? throw new InvalidOperationException("A tool part must carry a call id.");
        string recordId = EnsureUniqueRecordId(
            seenRecordIds,
            string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{parent.SessionKey}:{message.Id}:{callId}"));
        DateTimeOffset startedAt = ResolveStartedAt(row.TimeCreatedMs, unit.LastWriteUtc, importedAtUtc) is DateTimeOffset partStart
            ? partStart
            : ResolveStartedAt(message.TimeCreatedMs, unit.LastWriteUtc, importedAtUtc) is DateTimeOffset messageStart
                ? messageStart
                : parentStartedAt;

        DateTimeOffset? endedAt = null;
        long? durationMs = null;
        if (part is { StartTimeMs: not null, EndTimeMs: not null } && part.EndTimeMs.Value >= part.StartTimeMs.Value)
        {
            durationMs = part.EndTimeMs.Value - part.StartTimeMs.Value;
            try
            {
                endedAt = startedAt.AddMilliseconds((double)durationMs.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                endedAt = null;
                durationMs = null;
            }
        }

        CollectedToolContent? input = part.InputJson is not null
            ? ToolContentMeasurer.Measure(part.InputJson, model: message.Data.ModelText).ToToolContent(includeFingerprint: true)
            : null;

        bool isError = string.Equals(part.Status, "error", StringComparison.Ordinal);
        bool hasOutput = !string.IsNullOrEmpty(part.OutputText);
        CollectedToolContent? result = hasOutput || isError
            ? ToolContentMeasurer.Measure(hasOutput ? part.OutputText : string.Empty, model: message.Data.ModelText).ToToolContent(includeFingerprint: false)
            : null;
        bool? succeeded = isError ? false : hasOutput ? true : null;
        CollectedError? error = isError
            ? new CollectedError(result?.PlainText ?? "Tool invocation failed.", null)
            : null;

        string toolName = SafeOptionalText(part.Tool) ?? "unknown";
        return new CollectedRecord(
            RecordId: recordId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: toolName,
            StartedAt: startedAt,
            Status: isError ? RecordStatus.Error : RecordStatus.Ok,
            SessionKey: parent.SessionKey,
            ParentRecordId: parent.RecordId,
            TraceId: parent.TraceId,
            Model: null,
            EndedAt: endedAt,
            ReportedDurationMs: durationMs,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: new CollectedToolCall(
                CallId: callId,
                ToolName: toolName,
                McpServerName: null,
                McpToolName: null,
                Input: input,
                Result: result,
                Succeeded: succeeded,
                Error: error,
                ExitCode: null),
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(
                ErrorMessage: error?.Message,
                EditFailures: null,
                CorrectionRule: null,
                DeclaredContextWindow: null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);
    }

    private CollectedRecord BuildAgentRecord(
        CollectedRecord tool,
        string spawnCallId,
        string agentName,
        string harnessId,
        HashSet<string> seenRecordIds)
    {
        string hashInput = string.Create(CultureInfo.InvariantCulture, $"subagent:{spawnCallId}:{agentName}");
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        string agentHash = Convert.ToHexStringLower(hashBytes)[..12];
        string candidate = string.Create(CultureInfo.InvariantCulture, $"{tool.RecordId}-a{agentHash}");

        if (seenRecordIds.Add(candidate))
        {
            return AgentRecord(tool, candidate, spawnCallId, agentName, harnessId);
        }

        int suffix = 2;
        while (true)
        {
            string disambiguated = string.Create(CultureInfo.InvariantCulture, $"{candidate}:{suffix.ToString(CultureInfo.InvariantCulture)}");
            if (seenRecordIds.Add(disambiguated))
            {
                return AgentRecord(tool, disambiguated, spawnCallId, agentName, harnessId);
            }

            suffix++;
        }
    }

    private static CollectedRecord AgentRecord(
        CollectedRecord tool,
        string recordId,
        string spawnCallId,
        string agentName,
        string harnessId) =>
        new(
            RecordId: recordId,
            HarnessId: harnessId,
            Op: RecordOp.AgentInvoke,
            Kind: RecordKind.Internal,
            Name: agentName,
            StartedAt: tool.StartedAt,
            Status: RecordStatus.Unset,
            SessionKey: tool.SessionKey,
            ParentRecordId: tool.RecordId,
            TraceId: tool.TraceId,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: new CollectedAgentInvocation(
                AgentName: agentName,
                SpawningCallId: spawnCallId,
                Succeeded: null,
                Error: null),
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);

    private static KiloMessageRow? ParseMessageRow(JsonElement entry, string? documentSessionId)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? id = ReadText(entry, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string sessionId = ReadText(entry, "session_id") ?? documentSessionId ?? string.Empty;
        long? timeCreatedMs = entry.TryGetProperty("time_created", out JsonElement created)
            ? ReadFiniteLong(created)
            : null;

        KiloMessageData? data = entry.TryGetProperty("data", out JsonElement dataElement)
            ? ParseMessageData(CoerceDataObject(dataElement))
            : null;
        if (data is null)
        {
            return null;
        }

        return new KiloMessageRow(id, sessionId, timeCreatedMs, data);
    }

    private static KiloPartRow? ParsePartRow(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? id = ReadText(entry, "id");
        string? messageId = ReadText(entry, "message_id");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        long? timeCreatedMs = entry.TryGetProperty("time_created", out JsonElement created)
            ? ReadFiniteLong(created)
            : null;

        KiloPartData? data = entry.TryGetProperty("data", out JsonElement dataElement)
            ? ParsePartData(CoerceDataObject(dataElement))
            : null;
        if (data is null)
        {
            return null;
        }

        return new KiloPartRow(id, messageId, timeCreatedMs, data);
    }

    private static JsonElement? CoerceDataObject(JsonElement dataElement)
    {
        if (dataElement.ValueKind == JsonValueKind.Object)
        {
            return dataElement;
        }

        if (dataElement.ValueKind == JsonValueKind.String)
        {
            string? raw = dataElement.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using JsonDocument reparsed = JsonDocument.Parse(raw);
                if (reparsed.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return reparsed.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static KiloMessageData? ParseMessageData(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        JsonElement obj = data.Value;
        return new KiloMessageData(
            Role: ReadText(obj, "role"),
            ModelId: ReadText(obj, "modelID")?.Trim(),
            ProviderId: ReadText(obj, "providerID")?.Trim(),
            ModelText: ResolveModelText(obj),
            Cost: obj.TryGetProperty("cost", out JsonElement cost) ? ReadFiniteDouble(cost) : null,
            InputTokens: ReadCounter(obj, [["tokens", "input"], ["usage", "input_tokens"], ["tokens_input"]]),
            OutputTokens: ReadCounter(obj, [["tokens", "output"], ["usage", "output_tokens"], ["tokens_output"]]),
            ReasoningTokens: ReadCounter(obj, [["tokens", "reasoning"], ["tokens_reasoning"]]),
            CacheReadTokens: ReadCounter(obj, [["tokens", "cache", "read"], ["usage", "cache_read_input_tokens"], ["tokens_cache_read"]]),
            CacheWriteTokens: ReadCounter(obj, [["tokens", "cache", "write"], ["usage", "cache_creation_input_tokens"], ["tokens_cache_write"]]),
            ParentId: ReadText(obj, "parentID"));
    }

    private static KiloPartData? ParsePartData(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        JsonElement obj = data.Value;
        string partType = ReadText(obj, "type") ?? string.Empty;
        string? tool = ReadText(obj, "tool");

        string? inputJson = null;
        string? skillName = null;
        string? subagentType = null;
        string outputText = string.Empty;
        string? errorText = null;
        string? status = null;
        long? startMs = null;
        long? endMs = null;

        if (obj.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.Object)
        {
            status = ReadText(state, "status");
            if (state.TryGetProperty("input", out JsonElement input)
                && input.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                inputJson = input.GetRawText();
                if (string.Equals(tool, "skill", StringComparison.Ordinal))
                {
                    skillName = ReadText(input, "name");
                }

                if (string.Equals(tool, "task", StringComparison.Ordinal))
                {
                    subagentType = ReadText(input, "subagent_type");
                }
            }

            if (state.TryGetProperty("output", out JsonElement output)
                && output.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                outputText = output.ValueKind == JsonValueKind.String
                    ? output.GetString() ?? string.Empty
                    : output.GetRawText();
            }

            errorText = ReadText(state, "error");
            if (string.IsNullOrEmpty(outputText) && !string.IsNullOrEmpty(errorText))
            {
                outputText = errorText;
            }

            if (state.TryGetProperty("time", out JsonElement time) && time.ValueKind == JsonValueKind.Object)
            {
                if (time.TryGetProperty("start", out JsonElement start))
                {
                    startMs = ReadFiniteLong(start);
                }

                if (time.TryGetProperty("end", out JsonElement end))
                {
                    endMs = ReadFiniteLong(end);
                }
            }
        }

        return new KiloPartData(
            PartType: partType,
            Text: ReadText(obj, "text"),
            Tool: tool,
            CallId: ReadText(obj, "callID"),
            InputJson: inputJson,
            OutputText: outputText,
            ErrorText: errorText,
            Status: status,
            StartTimeMs: startMs,
            EndTimeMs: endMs,
            SkillName: skillName,
            SubagentType: subagentType);
    }

    private static KiloLegacyRequest? ParseLegacyRequest(JsonElement entry, int index)
    {
        string? rawCounters = ReadText(entry, "text");
        long tokensIn = 0;
        long tokensOut = 0;
        long cacheReads = 0;
        long cacheWrites = 0;
        double? cost = null;
        if (!string.IsNullOrWhiteSpace(rawCounters))
        {
            try
            {
                using JsonDocument counters = JsonDocument.Parse(rawCounters);
                if (counters.RootElement.ValueKind == JsonValueKind.Object)
                {
                    JsonElement obj = counters.RootElement;
                    tokensIn = ReadCounterField(obj, "tokensIn");
                    tokensOut = ReadCounterField(obj, "tokensOut");
                    cacheReads = ReadCounterField(obj, "cacheReads");
                    cacheWrites = ReadCounterField(obj, "cacheWrites");
                    cost = obj.TryGetProperty("cost", out JsonElement costElement) ? ReadFiniteDouble(costElement) : null;
                }
            }
            catch (JsonException)
            {
                // Malformed counter payloads behave as absent, matching the TypeScript reader.
            }
        }

        if (tokensIn == 0 && tokensOut == 0)
        {
            return null;
        }

        return new KiloLegacyRequest(
            Index: index,
            TokensIn: tokensIn,
            TokensOut: tokensOut,
            CacheReads: cacheReads,
            CacheWrites: cacheWrites,
            Cost: cost,
            Timestamp: ReadLegacyTimestamp(entry));
    }

    private static (string Model, string? Workspace) ParseCompanionHistory(string? companionText)
    {
        if (string.IsNullOrWhiteSpace(companionText))
        {
            return (LegacyFallbackModel, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(companionText);
        }
        catch (JsonException)
        {
            return (LegacyFallbackModel, null);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (LegacyFallbackModel, null);
            }

            string? model = null;
            string? workspace = null;
            foreach (JsonElement message in document.RootElement.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !string.Equals(ReadText(message, "role"), "user", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!message.TryGetProperty("content", out JsonElement content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? text = ReadText(block, "text");
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    model ??= ExtractHistoryModel(text);
                    workspace ??= ExtractHistoryWorkspace(text);
                    if (model is not null && workspace is not null)
                    {
                        break;
                    }
                }

                if (model is not null && workspace is not null)
                {
                    break;
                }
            }

            return (model ?? LegacyFallbackModel, workspace);
        }
    }

    private static string? ExtractHistoryModel(string text)
    {
        const string OpenTag = "<model>";
        const string CloseTag = "</model>";
        int open = text.IndexOf(OpenTag, StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        int close = text.IndexOf(CloseTag, open + OpenTag.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            return null;
        }

        string raw = text.Substring(open + OpenTag.Length, close - open - OpenTag.Length).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        int slash = raw.LastIndexOf('/');
        return slash >= 0 && slash + 1 < raw.Length ? raw.Substring(slash + 1) : raw;
    }

    private static string? ExtractHistoryWorkspace(string text)
    {
        const string Marker = "Current Workspace Directory (";
        int open = text.IndexOf(Marker, StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        int close = text.IndexOf(')', open + Marker.Length);
        if (close < 0)
        {
            return null;
        }

        string raw = text.Substring(open + Marker.Length, close - open - Marker.Length).Trim();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    private static string? ExtractTaskId(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return null;
        }

        string fileName = Path.GetFileName(sourceKey);
        if (string.Equals(fileName, "ui_messages.json", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetFileName(Path.GetDirectoryName(sourceKey));
            return SafeOptionalText(parent);
        }

        string stem = Path.GetFileNameWithoutExtension(sourceKey);
        return SafeOptionalText(stem);
    }

    private static string ResolveHarnessId(string? requestedHarnessId)
    {
        string normalized = HarnessSourceRegistry.NormalizeHarnessId(requestedHarnessId ?? string.Empty);
        if (string.Equals(normalized, KiloVscodeLegacyHarnessId, StringComparison.Ordinal)
            || string.Equals(normalized, KiloSharedRuntimeHarnessId, StringComparison.Ordinal))
        {
            return normalized;
        }

        return KiloSharedRuntimeHarnessId;
    }

    private static long TokenTotal(KiloMessageData data) =>
        data.InputTokens + data.OutputTokens + data.ReasoningTokens + data.CacheReadTokens + data.CacheWriteTokens;

    private static List<string> CollectTextParts(IReadOnlyList<KiloPartRow> parts)
    {
        List<string> texts = [];
        foreach (KiloPartRow row in parts)
        {
            string? text = PartText(row.Data);
            if (text is not null)
            {
                texts.Add(text);
            }
        }

        return texts;
    }

    private static string? PartText(KiloPartData part)
    {
        if (!string.Equals(part.PartType, "text", StringComparison.Ordinal)
            && !string.Equals(part.PartType, "markdown", StringComparison.Ordinal))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(part.Text) ? null : part.Text;
    }

    private static List<string> CollectToolResultTexts(IReadOnlyList<KiloPartRow> parts)
    {
        List<string> results = [];
        foreach (KiloPartRow row in parts)
        {
            KiloPartData part = row.Data;
            if (!string.Equals(part.PartType, "tool", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(part.OutputText))
            {
                results.Add(part.OutputText);
            }
        }

        return results;
    }

    private static long ReadCounter(JsonElement data, string[][] spellings)
    {
        foreach (string[] spelling in spellings)
        {
            JsonElement? candidate = Navigate(data, spelling);
            if (candidate.HasValue)
            {
                long? value = ReadFiniteLong(candidate.Value);
                if (value.HasValue)
                {
                    return value.Value;
                }
            }
        }

        return 0;
    }

    private static long ReadCounterField(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out JsonElement element) ? ReadFiniteLong(element) ?? 0 : 0;

    private static JsonElement? Navigate(JsonElement root, params string[] path)
    {
        JsonElement current = root;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out JsonElement next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static long? ReadFiniteLong(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out double value)
            && double.IsFinite(value))
        {
            if (value < 0)
            {
                return 0;
            }

            return (long)Math.Min(value, (double)long.MaxValue);
        }

        if (element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            && parsed >= 0)
        {
            return parsed;
        }

        return null;
    }

    private static double? ReadFiniteDouble(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out double value)
        && double.IsFinite(value)
            ? value
            : null;

    private static string? ReadText(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    private static string? ReadLegacyTimestamp(JsonElement entry)
    {
        if (!entry.TryGetProperty("ts", out JsonElement ts))
        {
            return null;
        }

        long? millis = ts.ValueKind switch
        {
            JsonValueKind.Number => ReadFiniteLong(ts),
            JsonValueKind.String => ParseTimestampText(ts.GetString()),
            _ => null,
        };

        if (millis is null)
        {
            return null;
        }

        return ToDateTimeOffset(millis) is DateTimeOffset instant
            ? instant.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : null;
    }

    private static long? ParseTimestampText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long millis))
        {
            return millis;
        }

        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed))
        {
            return parsed.ToUnixTimeMilliseconds();
        }

        return null;
    }

    private static string ResolveModelText(JsonElement data)
    {
        string? modelId = ReadText(data, "modelID")?.Trim();
        if (!string.IsNullOrEmpty(modelId))
        {
            string? providerId = ReadText(data, "providerID")?.Trim();
            if (!string.IsNullOrEmpty(providerId)
                && !string.Equals(modelId, providerId, StringComparison.Ordinal)
                && !modelId.StartsWith(providerId + "/", StringComparison.Ordinal))
            {
                return string.Create(CultureInfo.InvariantCulture, $"{providerId}/{modelId}");
            }

            return modelId;
        }

        if (data.TryGetProperty("model", out JsonElement model))
        {
            if (model.ValueKind == JsonValueKind.String
                && model.GetString() is { } modelText
                && !string.IsNullOrWhiteSpace(modelText))
            {
                return modelText.Trim();
            }

            if (model.ValueKind == JsonValueKind.Object)
            {
                string? id = ReadText(model, "id")?.Trim();
                string? provider = ReadText(model, "providerID")?.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    if (!string.IsNullOrEmpty(provider)
                        && !string.Equals(id, provider, StringComparison.Ordinal)
                        && !id.StartsWith(provider + "/", StringComparison.Ordinal))
                    {
                        return string.Create(CultureInfo.InvariantCulture, $"{provider}/{id}");
                    }

                    return id;
                }
            }
        }

        return UnknownModel;
    }

    private static DateTimeOffset? ToDateTimeOffset(long? timeCreatedMs)
    {
        if (timeCreatedMs is null or <= 0)
        {
            return null;
        }

        try
        {
            long millis = timeCreatedMs.Value < MillisThreshold ? timeCreatedMs.Value * 1000L : timeCreatedMs.Value;
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset ResolveStartedAt(long? timeCreatedMs, DateTimeOffset sourceLastWriteUtc, DateTimeOffset importedAtUtc)
    {
        if (ToDateTimeOffset(timeCreatedMs) is DateTimeOffset instant)
        {
            return instant;
        }

        if (sourceLastWriteUtc != default)
        {
            return sourceLastWriteUtc.ToUniversalTime();
        }

        return importedAtUtc.ToUniversalTime();
    }

    private static DateTimeOffset ResolveLegacyTimestamp(string? timestamp, DateTimeOffset sourceLastWriteUtc, DateTimeOffset importedAtUtc)
    {
        if (!string.IsNullOrWhiteSpace(timestamp)
            && DateTimeOffset.TryParse(
                timestamp,
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

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate) =>
        HarnessJson.EnsureUniqueRecordIdWithIndexSuffix(seenRecordIds, candidate);

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<KiloParseProblem> parseProblems,
        string sourceKey,
        string? sessionKey,
        string harnessId,
        DateTimeOffset importedAtUtc)
    {
        List<CollectedProblem> problems = new(parseProblems.Count);
        foreach (KiloParseProblem parseProblem in parseProblems)
        {
            string suffix = parseProblem.LineNumber > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $":line-{parseProblem.LineNumber.ToString(CultureInfo.InvariantCulture)}")
                : string.Empty;
            problems.Add(new CollectedProblem(
                ProblemKey: string.Create(
                    CultureInfo.InvariantCulture,
                    $"kilo-malformed:{sourceKey}{suffix}"),
                Severity: "warning",
                Code: parseProblem.Code,
                Message: parseProblem.Message,
                SpanId: null,
                Location: string.Create(CultureInfo.InvariantCulture, $"{sourceKey}{suffix}"),
                SessionKey: sessionKey,
                HarnessId: harnessId,
                TimestampUtc: FormatTimestamp(importedAtUtc),
                Stack: null));
        }

        return problems;
    }

    private static KiloParseProblem Fail(string detail) =>
        new(0, MalformedLineCode, $"Skipping malformed Kilo payload: {detail}");

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
