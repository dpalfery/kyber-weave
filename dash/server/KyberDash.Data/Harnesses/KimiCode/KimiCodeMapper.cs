using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.KimiCode;

/// <summary>
/// Provider-recorded parent/child attribution for one Kimi Code agent wire.
/// </summary>
/// <param name="ParentSessionId">Parent agent identifier (<c>main</c>) for child agents; <see langword="null"/> for roots.</param>
/// <param name="Role">Lineage role (<c>root</c> or <c>child</c>).</param>
/// <param name="Evidence">Attribution evidence (<c>provider-recorded</c>).</param>
public record KimiCodeLineage(
    string? ParentSessionId,
    string Role,
    string Evidence);

/// <summary>
/// Agent descriptor from the Kimi Code <c>state.json</c> <c>agents</c> map.
/// </summary>
/// <param name="ParentAgentId">Provider-recorded parent agent name (<see langword="null"/> for <c>main</c>).</param>
public record KimiCodeAgentDescriptor(
    string? ParentAgentId);

/// <summary>
/// Typed representation of the Kimi Code <c>state.json</c> session companion.
/// </summary>
/// <param name="CreatedAt">ISO-8601 UTC session creation timestamp (<c>createdAt</c>), when present.</param>
/// <param name="UpdatedAt">ISO-8601 UTC session update timestamp (<c>updatedAt</c>), when present.</param>
/// <param name="WorkDir">Session working directory (<c>workDir</c>), when present.</param>
/// <param name="Agents">Agent name to descriptor map (<c>agents</c>), when present.</param>
public record KimiCodeSessionState(
    string? CreatedAt,
    string? UpdatedAt,
    string? WorkDir,
    IReadOnlyDictionary<string, KimiCodeAgentDescriptor>? Agents);

/// <summary>
/// Normalized tool invocation observed on a Kimi Code wire turn.
/// </summary>
/// <param name="Name">Canonical tool name (<c>event.name</c> through the provider tool-name map).</param>
/// <param name="RawArgsText">Raw JSON arguments payload text (<c>event.args</c>), when present.</param>
/// <param name="BashCommand">Raw shell command (<c>event.args.command</c>) on <c>Bash</c> calls, when present.</param>
public record KimiCodeToolCall(
    string Name,
    string? RawArgsText,
    string? BashCommand);

/// <summary>
/// One billed Kimi Code <c>usage.record</c> event paired with its turn prompt and tools.
/// </summary>
/// <param name="LineNumber">1-based line number within <c>wire.jsonl</c>.</param>
/// <param name="UsageOrdinal">Zero-based ordinal of this billed event within the wire.</param>
/// <param name="Model">Resolved real model identifier (usage alias resolved through <c>llm.request</c>).</param>
/// <param name="ModelAlias">Raw usage alias (<c>usage.record.model</c>).</param>
/// <param name="TurnId">Turn identifier (<c>turnStep</c> before the first <c>.</c>), or <see langword="null"/> when absent.</param>
/// <param name="UserMessage">Paired turn prompt text (<c>turn.prompt.input</c>).</param>
/// <param name="Timestamp">ISO-8601 UTC event timestamp, resolved from usage, request, then session state.</param>
/// <param name="InputTokens">Exclusive input tokens (<c>usage.inputOther</c>).</param>
/// <param name="OutputTokens">Generated output tokens (<c>usage.output</c>).</param>
/// <param name="CacheReadTokens">Cache-read tokens (<c>usage.inputCacheRead</c>).</param>
/// <param name="CacheWriteTokens">Cache-creation tokens (<c>usage.inputCacheCreation</c>).</param>
/// <param name="Tools">Tool invocations observed since the previous billed event.</param>
public record KimiCodeCall(
    int LineNumber,
    int UsageOrdinal,
    string Model,
    string ModelAlias,
    string? TurnId,
    string UserMessage,
    string Timestamp,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    IReadOnlyList<KimiCodeToolCall> Tools);

/// <summary>
/// Non-fatal parse problem encountered while reading a Kimi Code wire or state payload.
/// </summary>
/// <param name="LineNumber">1-based wire line number (0 for document-level failures).</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record KimiCodeParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Parsed Kimi Code <c>wire.jsonl</c> document produced by <see cref="KimiCodeMapper.Parse"/>.
/// </summary>
/// <param name="Calls">Billed usage events in wire order.</param>
/// <param name="TotalLines">Count of non-blank wire lines processed.</param>
/// <param name="State">Parsed <c>state.json</c> companion, or <see langword="null"/> when absent or malformed.</param>
/// <param name="ParseProblems">Non-fatal line-level parse diagnostics.</param>
public record KimiCodeWireDocument(
    IReadOnlyList<KimiCodeCall> Calls,
    int TotalLines,
    KimiCodeSessionState? State,
    IReadOnlyList<KimiCodeParseProblem> ParseProblems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting <c>dash/src/providers/kimicode.ts</c> into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// The wire stream (<c>wire.jsonl</c>) arrives as <see cref="NativeSourceUnit.RawText"/>; the
/// <c>state.json</c> session companion arrives as <see cref="NativeSourceUnit.CompanionText"/> when
/// the caller supplies it. Only the offline-derivable semantics are ported: alias-resolved models,
/// per-turn prompts and tool calls, harness-reported exclusive token usage, and composer session
/// attribution. The wire format carries no reported cost, so published rates apply at read time;
/// provider-recorded agent lineage is exposed through <see cref="ResolveLineage"/> for
/// discovery-time attribution.
/// </remarks>
public class KimiCodeMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for Kimi Code.</summary>
    public const string KimiCodeHarnessId = "kimi-code";

    /// <summary>Diagnostic code emitted when a Kimi Code wire line cannot be parsed.</summary>
    public const string MalformedLineCode = "KIMICODE_MALFORMED_LINE";

    private const string DefaultModel = "kimicode-unknown";
    private const string SessionDirPrefix = "session_";
    private const string FallbackAgentId = "main";

    private static readonly IReadOnlyDictionary<string, string> ToolNameMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Bash"] = "Bash",
            ["Shell"] = "Bash",
            ["bash"] = "Bash",
            ["shell"] = "Bash",
            ["Read"] = "Read",
            ["ReadFile"] = "Read",
            ["read_file"] = "Read",
            ["Write"] = "Write",
            ["WriteFile"] = "Write",
            ["write_file"] = "Write",
            ["Edit"] = "Edit",
            ["EditFile"] = "Edit",
            ["edit_file"] = "Edit",
            ["Grep"] = "Grep",
            ["grep"] = "Grep",
            ["Glob"] = "Glob",
            ["glob"] = "Glob",
            ["Agent"] = "Agent",
            ["Task"] = "Agent",
        };

    /// <inheritdoc />
    public string HarnessId => KimiCodeHarnessId;

    /// <summary>
    /// Parses a Kimi Code <c>state.json</c> companion into a typed session state.
    /// Malformed or non-object payloads yield <see langword="null"/>, mirroring the deleted
    /// provider which treated an unreadable state as absent metadata.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the <c>state.json</c> companion.</param>
    /// <returns>The parsed session state, or <see langword="null"/> when absent or malformed.</returns>
    public static KimiCodeSessionState? ParseState(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement root = document.RootElement;
            Dictionary<string, KimiCodeAgentDescriptor>? agents = null;
            if (root.TryGetProperty("agents", out JsonElement agentsEl)
                && agentsEl.ValueKind == JsonValueKind.Object)
            {
                agents = new Dictionary<string, KimiCodeAgentDescriptor>(StringComparer.Ordinal);
                foreach (JsonProperty property in agentsEl.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? parentAgentId = null;
                    if (property.Value.TryGetProperty("parentAgentId", out JsonElement parentEl))
                    {
                        parentAgentId = parentEl.ValueKind == JsonValueKind.String
                            ? SafeOptionalText(parentEl.GetString())
                            : null;
                    }

                    agents[property.Name] = new KimiCodeAgentDescriptor(parentAgentId);
                }
            }

            return new KimiCodeSessionState(
                CreatedAt: SafeOptionalText(ReadStringOnly(root, "createdAt")),
                UpdatedAt: SafeOptionalText(ReadStringOnly(root, "updatedAt")),
                WorkDir: SafeOptionalText(ReadStringOnly(root, "workDir")),
                Agents: agents);
        }
    }

    /// <summary>
    /// Resolves provider-recorded parent/child lineage for one Kimi Code agent wire, mirroring
    /// <c>kimicodeLineageForSource</c> in the deleted provider.
    /// </summary>
    /// <param name="state">Parsed <c>state.json</c> companion.</param>
    /// <param name="agentId">Agent wire identifier (<c>main</c> for the session root).</param>
    /// <returns>The recorded lineage, or <see langword="null"/> when the state carries no <c>agents</c> map.</returns>
    public static KimiCodeLineage? ResolveLineage(KimiCodeSessionState? state, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (state?.Agents is null)
        {
            return null;
        }

        if (!string.Equals(agentId, FallbackAgentId, StringComparison.Ordinal))
        {
            if (!state.Agents.TryGetValue(agentId, out KimiCodeAgentDescriptor? self))
            {
                return null;
            }

            if (!string.Equals(self.ParentAgentId, FallbackAgentId, StringComparison.Ordinal))
            {
                return null;
            }

            return new KimiCodeLineage(ParentSessionId: FallbackAgentId, Role: "child", Evidence: "provider-recorded");
        }

        bool hasChild = state.Agents.Any(static kv =>
            !string.Equals(kv.Key, FallbackAgentId, StringComparison.Ordinal)
            && string.Equals(kv.Value.ParentAgentId, FallbackAgentId, StringComparison.Ordinal));
        return hasChild
            ? new KimiCodeLineage(ParentSessionId: null, Role: "root", Evidence: "provider-recorded")
            : null;
    }

    /// <summary>
    /// Parses a Kimi Code <c>wire.jsonl</c> stream into a strongly typed wire document.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the <c>wire.jsonl</c> stream.</param>
    /// <param name="companionText">Optional raw UTF-8 text of the <c>state.json</c> companion.</param>
    /// <returns>The parsed wire document.</returns>
    public static KimiCodeWireDocument Parse(string? rawText, string? companionText = null)
    {
        KimiCodeSessionState? state = ParseState(companionText);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new KimiCodeWireDocument([], 0, state, []);
        }

        List<KimiCodeCall> calls = [];
        List<KimiCodeParseProblem> problems = [];
        Dictionary<string, string> aliasModels = new(StringComparer.Ordinal);
        Dictionary<string, string> prompts = new(StringComparer.Ordinal);
        List<KimiCodeToolCall> pendingTools = [];
        string currentPrompt = string.Empty;
        string currentModel = string.Empty;
        string currentModelAlias = string.Empty;
        string currentTurnId = string.Empty;
        string? currentRequestTimestamp = null;
        int totalLines = 0;
        int usageOrdinal = 0;

        string? fallbackTimestamp = state?.UpdatedAt ?? state?.CreatedAt;

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

            totalLines++;

            JsonDocument lineDoc;
            try
            {
                lineDoc = JsonDocument.Parse(rawLine);
            }
            catch (JsonException ex)
            {
                problems.Add(Fail(lineNumber, $"Skipping malformed Kimi Code wire line {lineNumber}: {ex.Message}"));
                continue;
            }

            using (lineDoc)
            {
                if (lineDoc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(Fail(lineNumber, $"Skipping non-object Kimi Code wire line {lineNumber}."));
                    continue;
                }

                JsonElement record = lineDoc.RootElement;
                string eventType = ReadStringOnly(record, "type")?.Trim() ?? string.Empty;

                if (string.Equals(eventType, "turn.prompt", StringComparison.Ordinal))
                {
                    pendingTools = [];
                    currentPrompt = ReadInputText(record);
                    continue;
                }

                if (string.Equals(eventType, "llm.request", StringComparison.Ordinal))
                {
                    string model = ReadStringOnly(record, "model")?.Trim() ?? string.Empty;
                    string modelAlias = ReadStringOnly(record, "modelAlias")?.Trim() ?? string.Empty;
                    string turnId = TurnIdFromStep(ReadStringOnly(record, "turnStep"));
                    if (model.Length > 0 && modelAlias.Length > 0)
                    {
                        aliasModels[modelAlias] = model;
                    }

                    if (turnId.Length > 0 && currentPrompt.Length > 0)
                    {
                        prompts[turnId] = currentPrompt;
                    }

                    currentModel = model;
                    currentModelAlias = modelAlias;
                    currentTurnId = turnId;
                    currentRequestTimestamp = ReadTimestamp(record);
                    continue;
                }

                if (string.Equals(eventType, "context.append_loop_event", StringComparison.Ordinal))
                {
                    if (ReadToolDetails(record) is KimiCodeToolCall tool)
                    {
                        pendingTools.Add(tool);
                    }

                    continue;
                }

                if (!string.Equals(eventType, "usage.record", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!record.TryGetProperty("usage", out JsonElement usageEl)
                    || usageEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string usageAlias = ReadStringOnly(record, "model")?.Trim() ?? string.Empty;
                string realModel = aliasModels.TryGetValue(usageAlias, out string? aliased)
                    ? aliased
                    : currentModel.Length > 0 ? currentModel : DefaultModel;
                string timestamp = ReadTimestamp(record)
                    ?? currentRequestTimestamp
                    ?? fallbackTimestamp
                    ?? string.Empty;
                if (timestamp.Length == 0)
                {
                    pendingTools = [];
                    continue;
                }

                string? resolvedTurnId = currentTurnId.Length > 0 ? currentTurnId : null;
                string userMessage = resolvedTurnId is not null && prompts.TryGetValue(resolvedTurnId, out string? prompted)
                    ? prompted
                    : currentPrompt;

                calls.Add(new KimiCodeCall(
                    LineNumber: lineNumber,
                    UsageOrdinal: usageOrdinal,
                    Model: realModel,
                    ModelAlias: usageAlias,
                    TurnId: resolvedTurnId,
                    UserMessage: userMessage,
                    Timestamp: timestamp,
                    InputTokens: ReadNonNegativeNumber(usageEl, "inputOther"),
                    OutputTokens: ReadNonNegativeNumber(usageEl, "output"),
                    CacheReadTokens: ReadNonNegativeNumber(usageEl, "inputCacheRead"),
                    CacheWriteTokens: ReadNonNegativeNumber(usageEl, "inputCacheCreation"),
                    Tools: [.. pendingTools]));
                usageOrdinal++;
                pendingTools = [];
            }
        }

        return new KimiCodeWireDocument(calls, totalLines, state, problems);
    }

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        CollectedSource source = new(KimiCodeHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);
        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        KimiCodeWireDocument document = Parse(unit.RawText, unit.CompanionText);
        string sessionId = ResolveSessionId(unit.Unit.SourceKey);
        string agentId = ResolveAgentId(unit.Unit.SourceKey);
        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{KimiCodeHarnessId}:{sessionId}");
        string? workingDirectory = SafeOptionalText(document.State?.WorkDir);

        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (KimiCodeCall call in document.Calls)
        {
            DateTimeOffset? startedAt = TryParseTimestamp(call.Timestamp);
            if (!startedAt.HasValue)
            {
                continue;
            }

            string parentRecordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{CanonSynth.SynthSpanPrefix}{KimiCodeHarnessId}:{sessionId}:{agentId}:{call.LineNumber}:{call.UsageOrdinal}"),
                call.LineNumber);

            List<CollectedContentPart> parts = [];
            string? correctionRule = null;
            if (!string.IsNullOrEmpty(call.UserMessage))
            {
                CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(call.UserMessage);
                if (check.Matched)
                {
                    correctionRule = check.Rule;
                }

                parts.Add(BuildContentPart(0, ContentBucket.UserMessage, call.UserMessage, "user"));
            }

            List<CollectedTokenCount> tokens =
            [
                new CollectedTokenCount(TokenClass.Input, call.InputTokens),
                new CollectedTokenCount(TokenClass.Output, call.OutputTokens),
                new CollectedTokenCount(TokenClass.CacheRead, call.CacheReadTokens),
                new CollectedTokenCount(TokenClass.CacheWrite, call.CacheWriteTokens),
            ];

            CollectedRecord parentRecord = new(
                RecordId: parentRecordId,
                HarnessId: KimiCodeHarnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Internal,
                Name: string.Create(CultureInfo.InvariantCulture, $"{KimiCodeHarnessId}:{call.Model}"),
                StartedAt: startedAt.Value,
                Status: RecordStatus.Ok,
                SessionKey: sessionId,
                ParentRecordId: null,
                TraceId: traceId,
                Model: call.Model,
                EndedAt: null,
                ReportedDurationMs: null,
                Tokens: tokens,
                ReportedCost: null,
                Parts: parts,
                Tool: null,
                Agent: null,
                SkillsInvoked: [],
                AgentInstanceId: null,
                ToolDefinitions: [],
                Signals: new CollectedRecordSignals(null, null, correctionRule, null),
                ReportedInputTokens: call.InputTokens + call.CacheReadTokens + call.CacheWriteTokens,
                ReportedOutputTokens: call.OutputTokens);

            records.Add(parentRecord);

            for (int t = 0; t < call.Tools.Count; t++)
            {
                records.Add(BuildToolInvokeRecord(parentRecord, sessionId, traceId, call.Tools[t], t, seenRecordIds));
            }
        }

        List<CollectedSession> sessions = [];
        if (records.Count > 0 || document.TotalLines > 0)
        {
            CollectedWorkspace? workspace = workingDirectory is not null
                ? new CollectedWorkspace(workingDirectory, null, null)
                : null;

            sessions.Add(new CollectedSession(
                SessionKey: sessionId,
                HarnessId: KimiCodeHarnessId,
                NativeSessionId: sessionId,
                Workspace: workspace,
                Title: null,
                ReportedUsage: []));
        }

        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, sessionId, importedAtUtc);
        return new CollectedBatch(source, sessions, records, problems);
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parentRecord,
        string sessionId,
        string traceId,
        KimiCodeToolCall toolCall,
        int toolIndex,
        HashSet<string> seenRecordIds)
    {
        string rawInput = toolCall.RawArgsText ?? "{}";
        byte[] hashBytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{toolIndex}:{toolCall.Name}:{rawInput}"));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];
        string childRecordId = EnsureUniqueRecordId(
            seenRecordIds,
            string.Format(CultureInfo.InvariantCulture, "{0}-t{1}", parentRecord.RecordId, toolHash),
            toolIndex);

        CollectedToolContent? inputContent = toolCall.RawArgsText is not null
            ? ToolContentMeasurer.Measure(toolCall.RawArgsText, CanonSynth.MaxToolArgumentsBytes)
                .ToToolContent(includeFingerprint: true)
            : null;

        CollectedToolCall collectedTool = new(
            CallId: string.Create(CultureInfo.InvariantCulture, $"call-{toolIndex}"),
            ToolName: toolCall.Name,
            McpServerName: null,
            McpToolName: null,
            Input: inputContent,
            Result: null,
            Succeeded: null,
            Error: null,
            ExitCode: null);

        return new CollectedRecord(
            RecordId: childRecordId,
            HarnessId: KimiCodeHarnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: toolCall.Name,
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

    private static CollectedContentPart BuildContentPart(
        int position,
        ContentBucket bucket,
        string rawText,
        string role)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(rawText, CanonSynth.MaxToolResultBytes);
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

    private static string ResolveSessionId(string sourceKey)
    {
        string? sessionDir = SafeOptionalText(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(sourceKey))));
        string? candidate = SafeOptionalText(sessionDir is not null ? Path.GetFileName(sessionDir) : null);
        if (candidate is not null)
        {
            string stripped = candidate.StartsWith(SessionDirPrefix, StringComparison.Ordinal)
                ? candidate[SessionDirPrefix.Length..]
                : candidate;
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                return stripped;
            }
        }

        string? fileStem = SafeOptionalText(Path.GetFileNameWithoutExtension(sourceKey));
        if (fileStem is not null)
        {
            return fileStem;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return string.Create(CultureInfo.InvariantCulture, $"kimi-code-{Convert.ToHexStringLower(digest)[..16]}");
    }

    private static string ResolveAgentId(string sourceKey)
    {
        string? agentDir = SafeOptionalText(Path.GetDirectoryName(sourceKey));
        string? candidate = SafeOptionalText(agentDir is not null ? Path.GetFileName(agentDir) : null);
        return candidate ?? FallbackAgentId;
    }

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator) =>
        HarnessJson.EnsureUniqueRecordId(seenRecordIds, candidate, disambiguator);

    private static DateTimeOffset? TryParseTimestamp(string? rawTimestamp) =>
        HarnessJson.TryParseTimestamp(rawTimestamp);

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<KimiCodeParseProblem> parseProblems,
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

        foreach (KimiCodeParseProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"kimi-code-malformed:{sourceKey}:line-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed Kimi Code wire line.";

            problems.Add(new CollectedProblem(
                ProblemKey: problemKey,
                Severity: "warning",
                Code: parseProblem.Code,
                Message: message,
                SpanId: null,
                Location: location,
                SessionKey: sessionId,
                HarnessId: KimiCodeHarnessId,
                TimestampUtc: timestampUtc,
                Stack: null));
        }

        return problems;
    }

    private static KimiCodeParseProblem Fail(int lineNumber, string message) =>
        new(LineNumber: lineNumber, Code: MalformedLineCode, Message: message);

    private static string TurnIdFromStep(string? turnStep)
    {
        string step = turnStep?.Trim() ?? string.Empty;
        if (step.Length == 0)
        {
            return string.Empty;
        }

        int dot = step.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? step : step[..dot];
    }

    private static string ReadInputText(JsonElement record)
    {
        if (!record.TryGetProperty("input", out JsonElement inputEl))
        {
            return string.Empty;
        }

        if (inputEl.ValueKind == JsonValueKind.String)
        {
            return inputEl.GetString() ?? string.Empty;
        }

        if (inputEl.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        List<string> texts = [];
        foreach (JsonElement part in inputEl.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && string.Equals(ReadStringOnly(part, "type"), "text", StringComparison.Ordinal))
            {
                string? text = ReadStringOnly(part, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    texts.Add(text);
                }
            }
        }

        return string.Join('\n', texts);
    }

    private static KimiCodeToolCall? ReadToolDetails(JsonElement record)
    {
        if (!record.TryGetProperty("event", out JsonElement eventEl)
            || eventEl.ValueKind != JsonValueKind.Object
            || !string.Equals(ReadStringOnly(eventEl, "type"), "tool.call", StringComparison.Ordinal))
        {
            return null;
        }

        string rawName = ReadStringOnly(eventEl, "name")?.Trim() ?? string.Empty;
        if (rawName.Length == 0)
        {
            return null;
        }

        string name = ToolNameMap.TryGetValue(rawName, out string? mapped) ? mapped : rawName;

        string? rawArgsText = null;
        string? command = null;
        if (eventEl.TryGetProperty("args", out JsonElement argsEl))
        {
            if (argsEl.ValueKind == JsonValueKind.Object)
            {
                rawArgsText = argsEl.GetRawText();
                command = ReadStringOnly(argsEl, "command");
            }
            else if (argsEl.ValueKind == JsonValueKind.String)
            {
                string? argsText = argsEl.GetString();
                if (argsText is not null)
                {
                    try
                    {
                        using JsonDocument parsed = JsonDocument.Parse(argsText);
                        if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            rawArgsText = argsText;
                            command = ReadStringOnly(parsed.RootElement, "command");
                        }
                    }
                    catch (JsonException)
                    {
                        rawArgsText = argsText;
                    }
                }
            }
        }

        string? bashCommand = string.Equals(name, "Bash", StringComparison.Ordinal)
            ? SafeOptionalText(command)
            : null;

        return new KimiCodeToolCall(name, rawArgsText, bashCommand);
    }

    private static string? ReadTimestamp(JsonElement record)
    {
        if (!record.TryGetProperty("time", out JsonElement timeEl))
        {
            return null;
        }

        if (timeEl.ValueKind == JsonValueKind.String)
        {
            string? text = SafeOptionalText(timeEl.GetString());
            if (text is null)
            {
                return null;
            }

            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out parsed))
            {
                return parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            }

            return null;
        }

        if (timeEl.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        double millis;
        if (timeEl.TryGetInt64(out long millisInt))
        {
            millis = millisInt;
        }
        else if (timeEl.TryGetDouble(out double millisDouble) && double.IsFinite(millisDouble))
        {
            millis = millisDouble;
        }
        else
        {
            return null;
        }

        double resolvedMillis = millis > 1_000_000_000_000 ? millis : millis * 1000;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)resolvedMillis)
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static long ReadNonNegativeNumber(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el))
        {
            return 0;
        }

        double value;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt64(out long intValue))
            {
                return intValue >= 0 ? intValue : 0;
            }

            if (el.TryGetDouble(out double doubleValue) && double.IsFinite(doubleValue) && doubleValue >= 0)
            {
                return (long)doubleValue;
            }

            return 0;
        }

        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(
                el.GetString()?.Trim(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value)
            && double.IsFinite(value)
            && value >= 0)
        {
            return (long)value;
        }

        return 0;
    }

    private static string? ReadStringOnly(JsonElement obj, string key) =>
        HarnessJson.ReadStringOnly(obj, key);

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);
}
