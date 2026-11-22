using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.RooCode;

/// <summary>
/// Model and workspace metadata extracted from a Roo Code <c>api_conversation_history.json</c> companion.
/// </summary>
/// <param name="Model">Resolved model identifier (provider-qualified tag stripped to its bare name).</param>
/// <param name="Workspace">Workspace directory from <c>Current Workspace Directory (...)</c>, when present.</param>
public record RooCodeHistoryMeta(
    string Model,
    string? Workspace);

/// <summary>
/// One Roo Code <c>api_req_started</c> billing entry from <c>ui_messages.json</c>.
/// </summary>
/// <param name="Index">Zero-based position among <c>api_req_started</c> entries in the task.</param>
/// <param name="InputTokens">Harness-reported input tokens (<c>tokensIn</c>), when present.</param>
/// <param name="OutputTokens">Harness-reported output tokens (<c>tokensOut</c>), when present.</param>
/// <param name="CacheReadTokens">Cache-read tokens (<c>cacheReads</c>), when present.</param>
/// <param name="CacheWriteTokens">Cache-write tokens (<c>cacheWrites</c>), when present.</param>
/// <param name="ReportedCostUsd">Harness-reported USD cost (<c>cost</c>), when present.</param>
/// <param name="Timestamp">ISO-8601 UTC entry timestamp (<c>ts</c>), or <see langword="null"/> when missing or invalid.</param>
public record RooCodeApiRequest(
    int Index,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    double? ReportedCostUsd,
    string? Timestamp);

/// <summary>
/// Non-fatal parse problem encountered while reading a Roo Code task payload.
/// </summary>
/// <param name="LineNumber">Entry index within <c>ui_messages.json</c> (0 for document-level failures).</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record RooCodeParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Parsed Roo Code task document produced by <see cref="RooCodeMapper.Parse"/>.
/// </summary>
/// <param name="TaskId">Native task directory name.</param>
/// <param name="UserMessage">First user prompt text (<c>user_feedback</c> or <c>text</c>, first 500 characters).</param>
/// <param name="Model">Resolved model identifier (<c>cline-auto</c> fallback).</param>
/// <param name="Workspace">Workspace directory from the history companion, when present.</param>
/// <param name="MessageCount">Count of entries in <c>ui_messages.json</c>.</param>
/// <param name="Requests">Billing entries in task order.</param>
/// <param name="ParseProblems">Non-fatal document-level parse diagnostics.</param>
public record RooCodeTaskDocument(
    string TaskId,
    string UserMessage,
    string Model,
    string? Workspace,
    int MessageCount,
    IReadOnlyList<RooCodeApiRequest> Requests,
    IReadOnlyList<RooCodeParseProblem> ParseProblems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting <c>dash/src/providers/roo-code.ts</c> (via the shared
/// cline task grammar in <c>vscode-cline-parser.ts</c>) into normalized <see cref="CollectedBatch"/>
/// records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// The task's <c>ui_messages.json</c> array arrives as <see cref="NativeSourceUnit.RawText"/>; the
/// <c>api_conversation_history.json</c> companion arrives as <see cref="NativeSourceUnit.CompanionText"/>
/// when the caller supplies it. Only the offline-derivable semantics are ported: per-request
/// harness-reported usage, harness-reported cost, the first user prompt, and history-derived model
/// and workspace attribution. Cost estimation from the model table is a read-time concern, so
/// requests without a harness-reported cost carry no <c>ReportedCost</c>.
/// </remarks>
public partial class RooCodeMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for Roo Code.</summary>
    public const string RooCodeHarnessId = "roo-code";

    /// <summary>Diagnostic code emitted when a Roo Code task payload cannot be parsed.</summary>
    public const string MalformedLineCode = "ROOCODE_MALFORMED_LINE";

    /// <summary>Model fallback when no model indicator is present.</summary>
    public const string FallbackModel = "cline-auto";

    private const int MaxUserTextLength = 500;

    /// <inheritdoc />
    public string HarnessId => RooCodeHarnessId;

    /// <summary>
    /// Parses a Roo Code task into a strongly typed task document.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the task's <c>ui_messages.json</c> array.</param>
    /// <param name="taskId">Native task directory name.</param>
    /// <param name="companionText">Optional raw UTF-8 text of the <c>api_conversation_history.json</c> companion.</param>
    /// <returns>The parsed task document.</returns>
    public static RooCodeTaskDocument Parse(string? rawText, string taskId, string? companionText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        RooCodeHistoryMeta meta = ParseHistoryMeta(companionText);

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new RooCodeTaskDocument(taskId, string.Empty, meta.Model, meta.Workspace, 0, [], []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException ex)
        {
            return new RooCodeTaskDocument(
                taskId,
                string.Empty,
                meta.Model,
                meta.Workspace,
                0,
                [],
                [Fail(0, $"Skipping malformed Roo Code ui_messages.json export: {ex.Message}")]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new RooCodeTaskDocument(
                    taskId,
                    string.Empty,
                    meta.Model,
                    meta.Workspace,
                    0,
                    [],
                    [Fail(0, "Skipping Roo Code ui_messages.json export: expected a top-level JSON array of messages.")]);
            }

            int messageCount = document.RootElement.GetArrayLength();
            string userMessage = string.Empty;
            List<RooCodeApiRequest> requests = [];
            int requestIndex = 0;

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string entryType = ReadStringOnly(element, "type")?.Trim() ?? string.Empty;
                if (!string.Equals(entryType, "say", StringComparison.Ordinal))
                {
                    continue;
                }

                string say = ReadStringOnly(element, "say")?.Trim() ?? string.Empty;
                if ((string.Equals(say, "user_feedback", StringComparison.Ordinal)
                    || string.Equals(say, "text", StringComparison.Ordinal))
                    && userMessage.Length == 0)
                {
                    string? text = ReadStringOnly(element, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        userMessage = text.Length <= MaxUserTextLength ? text : text[..MaxUserTextLength];
                    }

                    continue;
                }

                if (!string.Equals(say, "api_req_started", StringComparison.Ordinal))
                {
                    continue;
                }

                long? tokensIn = null;
                long? tokensOut = null;
                long? cacheReads = null;
                long? cacheWrites = null;
                double? cost = null;

                string? payloadText = ReadStringOnly(element, "text");
                if (payloadText is not null)
                {
                    try
                    {
                        using JsonDocument payload = JsonDocument.Parse(payloadText);
                        if (payload.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            tokensIn = ReadOptionalNonNegativeLong(payload.RootElement, "tokensIn");
                            tokensOut = ReadOptionalNonNegativeLong(payload.RootElement, "tokensOut");
                            cacheReads = ReadOptionalNonNegativeLong(payload.RootElement, "cacheReads");
                            cacheWrites = ReadOptionalNonNegativeLong(payload.RootElement, "cacheWrites");
                            cost = ReadOptionalFiniteDouble(payload.RootElement, "cost");
                        }
                    }
                    catch (JsonException)
                    {
                        // Unparseable token payloads fall through to the zero-token skip below.
                    }
                }

                if ((tokensIn ?? 0) == 0 && (tokensOut ?? 0) == 0)
                {
                    continue;
                }

                requests.Add(new RooCodeApiRequest(
                    Index: requestIndex,
                    InputTokens: tokensIn,
                    OutputTokens: tokensOut,
                    CacheReadTokens: cacheReads,
                    CacheWriteTokens: cacheWrites,
                    ReportedCostUsd: cost,
                    Timestamp: ReadEntryTimestamp(element)));
                requestIndex++;
            }

            return new RooCodeTaskDocument(taskId, userMessage, meta.Model, meta.Workspace, messageCount, requests, []);
        }
    }

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        CollectedSource source = new(RooCodeHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);
        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        string taskId = ResolveTaskId(unit.Unit.SourceKey);
        RooCodeTaskDocument document = Parse(unit.RawText, taskId, unit.CompanionText);
        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{RooCodeHarnessId}:{taskId}");

        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (RooCodeApiRequest request in document.Requests)
        {
            DateTimeOffset startedAt = TryParseTimestamp(request.Timestamp)
                ?? (unit.LastWriteUtc == default ? importedAtUtc : unit.LastWriteUtc).ToUniversalTime();

            string parentRecordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{CanonSynth.SynthSpanPrefix}{RooCodeHarnessId}:{taskId}:{request.Index}"),
                request.Index);

            List<CollectedContentPart> parts = [];
            string? correctionRule = null;
            if (request.Index == 0 && !string.IsNullOrEmpty(document.UserMessage))
            {
                CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(document.UserMessage);
                if (check.Matched)
                {
                    correctionRule = check.Rule;
                }

                parts.Add(BuildContentPart(0, ContentBucket.UserMessage, document.UserMessage, "user"));
            }

            (IReadOnlyList<CollectedTokenCount> tokens, long? reportedInput, long? reportedOutput) = BuildTokens(request);
            CollectedCost? reportedCost = request.ReportedCostUsd is > 0 && double.IsFinite(request.ReportedCostUsd.Value)
                ? new CollectedCost(request.ReportedCostUsd.Value, "USD")
                : null;

            records.Add(new CollectedRecord(
                RecordId: parentRecordId,
                HarnessId: RooCodeHarnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Internal,
                Name: string.Create(CultureInfo.InvariantCulture, $"{RooCodeHarnessId}:{document.Model}"),
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
                Signals: new CollectedRecordSignals(null, null, correctionRule, null),
                ReportedInputTokens: reportedInput,
                ReportedOutputTokens: reportedOutput));
        }

        List<CollectedSession> sessions = [];
        if (records.Count > 0 || document.MessageCount > 0)
        {
            CollectedWorkspace? workspace = document.Workspace is not null
                ? new CollectedWorkspace(document.Workspace, null, null)
                : null;

            sessions.Add(new CollectedSession(
                SessionKey: taskId,
                HarnessId: RooCodeHarnessId,
                NativeSessionId: taskId,
                Workspace: workspace,
                Title: null,
                ReportedUsage: []));
        }

        List<CollectedProblem> problems = BuildProblems(document.ParseProblems, unit.Unit.SourceKey, taskId, importedAtUtc);
        return new CollectedBatch(source, sessions, records, problems);
    }

    private static RooCodeHistoryMeta ParseHistoryMeta(string? companionText)
    {
        if (string.IsNullOrWhiteSpace(companionText))
        {
            return new RooCodeHistoryMeta(FallbackModel, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(companionText);
        }
        catch (JsonException)
        {
            return new RooCodeHistoryMeta(FallbackModel, null);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new RooCodeHistoryMeta(FallbackModel, null);
            }

            string? model = null;
            string? workspace = null;

            foreach (JsonElement message in document.RootElement.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !string.Equals(ReadStringOnly(message, "role"), "user", StringComparison.Ordinal)
                    || !message.TryGetProperty("content", out JsonElement contentEl)
                    || contentEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement block in contentEl.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? text = ReadStringOnly(block, "text");
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    model ??= ExtractBareModel(text);
                    workspace ??= ExtractWorkspace(text);

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

            return new RooCodeHistoryMeta(model ?? FallbackModel, workspace);
        }
    }

    private static string? ExtractBareModel(string text)
    {
        Match match = ModelTagRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        string tag = match.Groups[1].Value.Trim();
        if (tag.Length == 0)
        {
            return null;
        }

        int slash = tag.LastIndexOf('/');
        string bare = slash >= 0 ? tag[(slash + 1)..].Trim() : tag;
        return bare.Length > 0 ? bare : null;
    }

    private static string? ExtractWorkspace(string text)
    {
        Match match = WorkspaceDirRegex().Match(text);
        return match.Success ? SafeOptionalText(match.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"<model>([^<]+)</model>", RegexOptions.None)]
    private static partial Regex ModelTagRegex();

    [GeneratedRegex(@"Current Workspace Directory \(([^)]+)\)", RegexOptions.None)]
    private static partial Regex WorkspaceDirRegex();

    private static (IReadOnlyList<CollectedTokenCount> Tokens, long? ReportedInput, long? ReportedOutput) BuildTokens(RooCodeApiRequest request)
    {
        List<CollectedTokenCount> tokens = [];
        long inputSum = 0L;
        bool hasInput = false;

        if (request.InputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, request.InputTokens.Value));
            inputSum += request.InputTokens.Value;
            hasInput = true;
        }

        if (request.CacheReadTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, request.CacheReadTokens.Value));
            inputSum += request.CacheReadTokens.Value;
            hasInput = true;
        }

        if (request.CacheWriteTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, request.CacheWriteTokens.Value));
            inputSum += request.CacheWriteTokens.Value;
            hasInput = true;
        }

        if (request.OutputTokens.HasValue)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, request.OutputTokens.Value));
        }

        return (tokens, hasInput ? inputSum : null, request.OutputTokens);
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

    private static string ResolveTaskId(string sourceKey)
    {
        string trimmed = sourceKey.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? candidate = SafeOptionalText(Path.GetFileName(trimmed));
        if (candidate is not null)
        {
            return candidate;
        }

        string? fileStem = SafeOptionalText(Path.GetFileNameWithoutExtension(trimmed));
        if (fileStem is not null)
        {
            return fileStem;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return string.Create(CultureInfo.InvariantCulture, $"roo-code-{Convert.ToHexStringLower(digest)[..16]}");
    }

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator) =>
        HarnessJson.EnsureUniqueRecordId(seenRecordIds, candidate, disambiguator);

    private static DateTimeOffset? TryParseTimestamp(string? rawTimestamp) =>
        HarnessJson.TryParseTimestamp(rawTimestamp);

    private static string? ReadEntryTimestamp(JsonElement element)
    {
        if (!element.TryGetProperty("ts", out JsonElement tsEl))
        {
            return null;
        }

        if (tsEl.ValueKind == JsonValueKind.Number)
        {
            if (!tsEl.TryGetInt64(out long millis))
            {
                if (!tsEl.TryGetDouble(out double millisDouble) || !double.IsFinite(millisDouble))
                {
                    return null;
                }

                millis = (long)millisDouble;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(millis)
                    .ToUniversalTime()
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        if (tsEl.ValueKind == JsonValueKind.String)
        {
            string? text = SafeOptionalText(tsEl.GetString());
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

        return null;
    }

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<RooCodeParseProblem> parseProblems,
        string sourceKey,
        string taskId,
        DateTimeOffset importedAtUtc)
    {
        if (parseProblems.Count == 0)
        {
            return [];
        }

        string timestampUtc = importedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        List<CollectedProblem> problems = new(parseProblems.Count);

        foreach (RooCodeParseProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"roo-code-malformed:{sourceKey}:line-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed Roo Code task payload.";

            problems.Add(new CollectedProblem(
                ProblemKey: problemKey,
                Severity: "warning",
                Code: parseProblem.Code,
                Message: message,
                SpanId: null,
                Location: location,
                SessionKey: taskId,
                HarnessId: RooCodeHarnessId,
                TimestampUtc: timestampUtc,
                Stack: null));
        }

        return problems;
    }

    private static RooCodeParseProblem Fail(int lineNumber, string message) =>
        new(LineNumber: lineNumber, Code: MalformedLineCode, Message: message);

    private static string? ReadStringOnly(JsonElement obj, string key) =>
        HarnessJson.ReadStringOnly(obj, key);

    private static long? ReadOptionalNonNegativeLong(JsonElement obj, string key) =>
        HarnessJson.ReadOptionalNonNegativeLong(obj, key);

    private static double? ReadOptionalFiniteDouble(JsonElement obj, string key) =>
        HarnessJson.ReadOptionalDouble(obj, key);

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);
}
