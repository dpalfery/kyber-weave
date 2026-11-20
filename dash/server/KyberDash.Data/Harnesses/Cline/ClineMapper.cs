using System.Globalization;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Cline;

/// <summary>
/// Non-fatal parse problem encountered while reading a Cline native surface.
/// </summary>
/// <param name="LineNumber">1-based element or message ordinal within the native payload (0 for document-level failures).</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record ClineParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// One usage-bearing <c>api_req_started</c> entry from a Cline <c>ui_messages.json</c> task file,
/// mirroring the <c>createClineParser</c> projection in <c>dash/src/providers/vscode-cline-parser.ts</c>.
/// </summary>
/// <param name="Index">Zero-based position among <c>api_req_started</c> entries in the file.</param>
/// <param name="Timestamp">Entry timestamp (<c>ts</c> milliseconds), falling back to the unit timestamp when absent or invalid.</param>
/// <param name="InputTokens">Harness-reported input tokens (<c>tokensIn</c>).</param>
/// <param name="OutputTokens">Harness-reported output tokens (<c>tokensOut</c>).</param>
/// <param name="CacheReadTokens">Harness-reported cache-read tokens (<c>cacheReads</c>).</param>
/// <param name="CacheWriteTokens">Harness-reported cache-write tokens (<c>cacheWrites</c>).</param>
/// <param name="Cost">Harness-reported cost (<c>cost</c>), or <see langword="null"/> when absent or negative.</param>
public record ClineUsageEntry(
    int Index,
    DateTimeOffset Timestamp,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    double? Cost);

/// <summary>
/// Parsed Cline VS Code extension task (<c>tasks/&lt;id&gt;/ui_messages.json</c>) produced
/// by <see cref="ClineTaskDocument.Parse"/>.
/// </summary>
/// <param name="TaskId">Task directory name (<c>basename(taskDir)</c>).</param>
/// <param name="UserMessage">First <c>user_feedback</c>/<c>text</c> message text (up to 500 characters).</param>
/// <param name="Entries">Usage-bearing <c>api_req_started</c> entries in file order.</param>
/// <param name="ParseProblems">Non-fatal element-level parse diagnostics.</param>
public record ClineTaskDocument(
    string TaskId,
    string UserMessage,
    IReadOnlyList<ClineUsageEntry> Entries,
    IReadOnlyList<ClineParseProblem> ParseProblems)
{
    /// <summary>
    /// Parses a Cline <c>ui_messages.json</c> task file: the first <c>user_feedback</c>/<c>text</c>
    /// message seeds <see cref="UserMessage"/>, and every <c>api_req_started</c> entry carrying
    /// input or output tokens becomes a <see cref="ClineUsageEntry"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the <c>ui_messages.json</c> file.</param>
    /// <param name="sourceKey">Source locator; the parent directory name seeds <see cref="TaskId"/>.</param>
    /// <param name="fallbackTimestamp">Timestamp used when an entry carries no parseable <c>ts</c>.</param>
    /// <returns>The parsed task document.</returns>
    public static ClineTaskDocument Parse(string? rawText, string sourceKey, DateTimeOffset fallbackTimestamp)
    {
        string taskId = ClineMapper.TaskIdFromSourceKey(sourceKey);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new ClineTaskDocument(taskId, string.Empty, [], []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException ex)
        {
            return new ClineTaskDocument(taskId, string.Empty, [], [Fail(0, $"Skipping malformed Cline task file: {ex.Message}")]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ClineTaskDocument(
                    taskId,
                    string.Empty,
                    [],
                    [Fail(0, "Skipping Cline task file: expected a top-level JSON array of messages.")]);
            }

            List<JsonElement> elements = [.. document.RootElement.EnumerateArray()];
            string userMessage = string.Empty;
            foreach (JsonElement element in elements)
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!IsSay(element, "user_feedback") && !IsSay(element, "text"))
                {
                    continue;
                }

                string? text = ReadRawText(element, "text") ?? string.Empty;
                userMessage = text.Length <= ClineMapper.MaxUserTextLength ? text : text[..ClineMapper.MaxUserTextLength];
                break;
            }

            List<ClineUsageEntry> entries = [];
            List<ClineParseProblem> problems = [];
            int entryIndex = 0;
            for (int ordinal = 0; ordinal < elements.Count; ordinal++)
            {
                JsonElement element = elements[ordinal];
                if (element.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(Fail(ordinal + 1, $"Skipping non-object Cline task element {ordinal + 1}."));
                    continue;
                }

                if (!IsApiRequestStarted(element))
                {
                    continue;
                }

                if (!element.TryGetProperty("text", out JsonElement textElement)
                    || textElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? payload = textElement.GetString();
                if (string.IsNullOrEmpty(payload))
                {
                    continue;
                }

                JsonDocument payloadDocument;
                try
                {
                    payloadDocument = JsonDocument.Parse(payload);
                }
                catch (JsonException ex)
                {
                    problems.Add(Fail(ordinal + 1, $"Skipping malformed Cline api_req_started payload {ordinal + 1}: {ex.Message}"));
                    continue;
                }

                using (payloadDocument)
                {
                    if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        problems.Add(Fail(ordinal + 1, $"Skipping non-object Cline api_req_started payload {ordinal + 1}."));
                        continue;
                    }

                    JsonElement usage = payloadDocument.RootElement;
                    long tokensIn = ClineMapper.SafeTokenCount(usage, "tokensIn");
                    long tokensOut = ClineMapper.SafeTokenCount(usage, "tokensOut");
                    if (tokensIn == 0 && tokensOut == 0)
                    {
                        continue;
                    }

                    entries.Add(new ClineUsageEntry(
                        entryIndex++,
                        ClineMapper.ToTaskTimestamp(element, fallbackTimestamp),
                        tokensIn,
                        tokensOut,
                        ClineMapper.SafeTokenCount(usage, "cacheReads"),
                        ClineMapper.SafeTokenCount(usage, "cacheWrites"),
                        ClineMapper.ReadReportedCost(usage, "cost")));
                }
            }

            return new ClineTaskDocument(taskId, userMessage, entries, problems);
        }
    }

    private static bool IsSay(JsonElement element, string say)
    {
        return string.Equals(ReadRawText(element, "type"), "say", StringComparison.Ordinal)
            && string.Equals(ReadRawText(element, "say"), say, StringComparison.Ordinal);
    }

    private static bool IsApiRequestStarted(JsonElement element)
    {
        return string.Equals(ReadRawText(element, "type"), "say", StringComparison.Ordinal)
            && string.Equals(ReadRawText(element, "say"), "api_req_started", StringComparison.Ordinal);
    }

    private static string? ReadRawText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static ClineParseProblem Fail(int lineNumber, string message) =>
        new(LineNumber: lineNumber, Code: ClineMapper.MalformedLineCode, Message: message);
}

/// <summary>
/// One <c>tool_use</c> content block from a Cline CLI assistant message, mirroring the
/// <c>collectTools</c> projection in <c>dash/src/providers/cline-cli.ts</c>.
/// </summary>
/// <param name="CallId">Native tool call identifier (<c>id</c>), or a positional fallback.</param>
/// <param name="ToolName">Canonical tool name after <c>toolNameMap</c> mapping.</param>
/// <param name="InputJson">Raw tool input JSON text, when the block carried an object, array, or string input.</param>
public record ClineCliToolCall(
    string CallId,
    string ToolName,
    string? InputJson);

/// <summary>
/// One usage-bearing assistant message from a Cline CLI <c>*.messages.json</c> session file,
/// mirroring the per-message projection in <c>dash/src/providers/cline-cli.ts</c>.
/// </summary>
/// <param name="MessageId">Native message identifier (<c>id</c>), or a positional fallback.</param>
/// <param name="Timestamp">Message timestamp (<c>ts</c>), falling back to the unit timestamp when absent or invalid.</param>
/// <param name="Model">Model identifier (<c>modelInfo.id</c>), or <c>unknown</c> when absent.</param>
/// <param name="InputTokens">Harness-reported input tokens (<c>metrics.inputTokens</c>).</param>
/// <param name="OutputTokens">Harness-reported output tokens (<c>metrics.outputTokens</c>).</param>
/// <param name="CacheReadTokens">Harness-reported cache-read tokens (<c>metrics.cacheReadTokens</c>).</param>
/// <param name="CacheWriteTokens">Harness-reported cache-write tokens (<c>metrics.cacheWriteTokens</c>).</param>
/// <param name="Cost">Harness-reported cost (<c>metrics.cost</c>), or zero when absent.</param>
/// <param name="CostReported">True when <c>metrics.cost</c> was a finite non-negative number.</param>
/// <param name="Tools">Tool invocations observed on the message.</param>
/// <param name="Skills">Skill names invoked via the <c>Skill</c> tool.</param>
/// <param name="SubagentTypes">Agent type names invoked via <c>Agent</c> tools.</param>
/// <param name="WebSearchRequests">Count of <c>WebFetch</c> tool uses on the message.</param>
public record ClineCliMessage(
    string MessageId,
    DateTimeOffset Timestamp,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    double Cost,
    bool CostReported,
    IReadOnlyList<ClineCliToolCall> Tools,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> SubagentTypes,
    int WebSearchRequests);

/// <summary>
/// Parsed Cline CLI session (<c>sessions/&lt;id&gt;/&lt;id&gt;.messages.json</c>) produced
/// by <see cref="ClineCliDocument.Parse"/>.
/// </summary>
/// <param name="SessionId">Session identifier (messages file stem).</param>
/// <param name="UserMessage">First user-role message text.</param>
/// <param name="Messages">Usage-bearing assistant messages in file order.</param>
/// <param name="ParseProblems">Non-fatal message-level parse diagnostics.</param>
public record ClineCliDocument(
    string SessionId,
    string UserMessage,
    IReadOnlyList<ClineCliMessage> Messages,
    IReadOnlyList<ClineParseProblem> ParseProblems)
{
    /// <summary>
    /// Parses a Cline CLI <c>*.messages.json</c> session file: the first user-role message seeds
    /// <see cref="UserMessage"/>, and every assistant message carrying usage metrics becomes a
    /// <see cref="ClineCliMessage"/>. The per-session metadata sidecar (<c>&lt;id&gt;.json</c>)
    /// is not supplied to the mapper, so session model, workspace, and rollup usage are unavailable.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the <c>*.messages.json</c> file.</param>
    /// <param name="sourceKey">Source locator; the file stem seeds <see cref="SessionId"/>.</param>
    /// <param name="fallbackTimestamp">Timestamp used when a message carries no parseable <c>ts</c>.</param>
    /// <returns>The parsed CLI session document.</returns>
    public static ClineCliDocument Parse(string? rawText, string sourceKey, DateTimeOffset fallbackTimestamp)
    {
        string sessionId = ClineMapper.CliSessionIdFromSourceKey(sourceKey);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new ClineCliDocument(sessionId, string.Empty, [], []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException ex)
        {
            return new ClineCliDocument(sessionId, string.Empty, [], [Fail(0, $"Skipping malformed Cline CLI session file: {ex.Message}")]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ClineCliDocument(
                    sessionId,
                    string.Empty,
                    [],
                    [Fail(0, "Skipping Cline CLI session file: expected a top-level JSON object.")]);
            }

            List<JsonElement> messages = [];
            if (document.RootElement.TryGetProperty("messages", out JsonElement messagesElement)
                && messagesElement.ValueKind == JsonValueKind.Array)
            {
                messages.AddRange(messagesElement.EnumerateArray());
            }

            string userMessage = string.Empty;
            foreach (JsonElement message in messages)
            {
                if (message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!string.Equals(ClineMapper.ReadText(message, "role"), "user", StringComparison.Ordinal))
                {
                    continue;
                }

                string? text = ClineMapper.FirstTextContent(message);
                if (text is not null)
                {
                    userMessage = text;
                    break;
                }
            }

            List<ClineCliMessage> parsed = [];
            List<ClineParseProblem> problems = [];
            for (int ordinal = 0; ordinal < messages.Count; ordinal++)
            {
                JsonElement message = messages[ordinal];
                if (message.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(Fail(ordinal + 1, $"Skipping non-object Cline CLI message {ordinal + 1}."));
                    continue;
                }

                if (!string.Equals(ClineMapper.ReadText(message, "role"), "assistant", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!message.TryGetProperty("metrics", out JsonElement metrics)
                    || metrics.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                long inputTokens = ClineMapper.SafeTokenCount(metrics, "inputTokens");
                long outputTokens = ClineMapper.SafeTokenCount(metrics, "outputTokens");
                long cacheReadTokens = ClineMapper.SafeTokenCount(metrics, "cacheReadTokens");
                long cacheWriteTokens = ClineMapper.SafeTokenCount(metrics, "cacheWriteTokens");
                (double cost, bool costReported) = ClineMapper.ReadCliCost(metrics);
                if (inputTokens == 0 && outputTokens == 0 && cacheReadTokens == 0 && cacheWriteTokens == 0 && cost <= 0)
                {
                    continue;
                }

                string messageId = ClineMapper.ReadText(message, "id") ?? ordinal.ToString(CultureInfo.InvariantCulture);
                string model = ClineMapper.ReadModelId(message) ?? ClineMapper.DefaultCliModel;
                (List<ClineCliToolCall> tools, List<string> skills, List<string> subagentTypes, int webSearchRequests)
                    = ClineMapper.CollectTools(message, ordinal);

                parsed.Add(new ClineCliMessage(
                    messageId,
                    ClineMapper.ToCliTimestamp(message, fallbackTimestamp),
                    model,
                    inputTokens,
                    outputTokens,
                    cacheReadTokens,
                    cacheWriteTokens,
                    cost,
                    costReported,
                    tools,
                    skills,
                    subagentTypes,
                    webSearchRequests));
            }

            return new ClineCliDocument(sessionId, userMessage, parsed, problems);
        }
    }

    private static ClineParseProblem Fail(int lineNumber, string message) =>
        new(LineNumber: lineNumber, Code: ClineMapper.MalformedLineCode, Message: message);
}

/// <summary>
/// Pure <see cref="IHarnessMapper"/> translating Cline native surfaces into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock:
/// the VS Code extension <c>tasks/&lt;id&gt;/ui_messages.json</c> surface (<c>cline</c>) and the
/// Cline CLI <c>sessions/&lt;id&gt;/&lt;id&gt;.messages.json</c> surface (<c>cline-cli</c>).
/// </summary>
/// <remarks>
/// The metadata sidecars (<c>api_conversation_history.json</c> for <c>cline</c>,
/// <c>&lt;id&gt;.json</c> for <c>cline-cli</c>) are not supplied to the mapper, so the session model
/// falls back to <c>cline-auto</c> (<c>cline</c>) or <c>unknown</c> (<c>cline-cli</c>), no workspace
/// is recorded, and the CLI session-usage rollup fallback cannot apply: metric-less CLI messages
/// yield no records. Costs are persisted only when harness-reported; read-time pricing applies otherwise.
/// </remarks>
public class ClineMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for the Cline VS Code extension surface.</summary>
    public const string ClineHarnessId = "cline";

    /// <summary>Harness identifier for the Cline CLI surface.</summary>
    public const string ClineCliHarnessId = "cline-cli";

    /// <summary>Problem code emitted when a native Cline element cannot be parsed.</summary>
    public const string MalformedLineCode = "CLINE_MALFORMED_LINE";

    /// <summary>Problem code emitted when a unit holds no usage-bearing turns.</summary>
    public const string UnrecognizedTranscriptCode = "CLINE_UNRECOGNIZED";

    /// <summary>Maximum user prompt characters retained per session.</summary>
    internal const int MaxUserTextLength = 500;

    /// <summary>Fallback model for <c>cline</c> tasks (history metadata is not supplied to the mapper).</summary>
    internal const string DefaultTaskModel = "cline-auto";

    /// <summary>Fallback model for <c>cline-cli</c> messages (session metadata is not supplied to the mapper).</summary>
    internal const string DefaultCliModel = "unknown";

    private const long MinReasonableTimestampMs = 1_000_000_000_000L;
    private const double MaxSafeInteger = 9_007_199_254_740_991d;
    private const long MinUnixMilliseconds = -62_135_596_800_000L;
    private const long MaxUnixMilliseconds = 253_402_300_799_999L;

    private static readonly IReadOnlyDictionary<string, string> ToolNameMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run_commands"] = "Bash",
            ["read_files"] = "Read",
            ["editor"] = "Edit",
            ["apply_patch"] = "Edit",
            ["search_codebase"] = "Grep",
            ["fetch_web_content"] = "WebFetch",
            ["skills"] = "Skill",
            ["spawn_agent"] = "Agent",
            ["team_spawn_teammate"] = "Agent",
            ["team_run_task"] = "Agent",
            ["ask_question"] = "AskUser",
        };

    /// <inheritdoc />
    public string HarnessId => ClineHarnessId;

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        string harnessId = ResolveHarnessId(unit.Unit.HarnessId);
        CollectedSource source = new(harnessId, unit.Unit.Kind, unit.Unit.SourceKey);
        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        return string.Equals(harnessId, ClineCliHarnessId, StringComparison.Ordinal)
            ? MapCli(unit, source, importedAtUtc, harnessId)
            : MapTask(unit, source, importedAtUtc, harnessId);
    }

    private static string ResolveHarnessId(string? requestedHarnessId)
    {
        string normalized = HarnessSourceRegistry.NormalizeHarnessId(requestedHarnessId ?? string.Empty);
        if (string.Equals(normalized, ClineCliHarnessId, StringComparison.Ordinal))
        {
            return ClineCliHarnessId;
        }

        if (string.Equals(normalized, ClineHarnessId, StringComparison.Ordinal))
        {
            return ClineHarnessId;
        }

        return ClineHarnessId;
    }

    internal static string TaskIdFromSourceKey(string sourceKey)
    {
        string? directory = Path.GetDirectoryName(sourceKey);
        string? taskId = directory is null ? null : Path.GetFileName(directory);
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            return taskId;
        }

        string stem = Path.GetFileNameWithoutExtension(sourceKey);
        return string.IsNullOrWhiteSpace(stem) ? "unknown" : stem;
    }

    internal static string CliSessionIdFromSourceKey(string sourceKey)
    {
        string fileName = Path.GetFileName(sourceKey);
        string stem = fileName.EndsWith(".messages.json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".messages.json".Length]
            : Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrWhiteSpace(stem))
        {
            return stem;
        }

        string? directory = Path.GetDirectoryName(sourceKey);
        string? parent = directory is null ? null : Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(parent) ? "unknown" : parent;
    }

    internal static string? ReadText(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal static long SafeTokenCount(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double number)
            || double.IsNaN(number)
            || double.IsInfinity(number)
            || number < 0)
        {
            return 0;
        }

        return (long)Math.Floor(Math.Min(number, MaxSafeInteger));
    }

    internal static double? ReadReportedCost(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double number)
            || double.IsNaN(number)
            || double.IsInfinity(number)
            || number < 0)
        {
            return null;
        }

        return number;
    }

    internal static (double Cost, bool CostReported) ReadCliCost(JsonElement metrics)
    {
        if (metrics.ValueKind != JsonValueKind.Object
            || !metrics.TryGetProperty("cost", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double number)
            || double.IsNaN(number)
            || double.IsInfinity(number)
            || number < 0)
        {
            return (0, false);
        }

        return (number, true);
    }

    internal static DateTimeOffset ToTaskTimestamp(JsonElement element, DateTimeOffset fallback)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("ts", out JsonElement timestamp))
        {
            if (timestamp.ValueKind == JsonValueKind.Number
                && timestamp.TryGetDouble(out double milliseconds)
                && double.IsFinite(milliseconds)
                && milliseconds >= MinUnixMilliseconds
                && milliseconds <= MaxUnixMilliseconds)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Truncate(milliseconds)).ToUniversalTime();
            }

            if (timestamp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    timestamp.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return fallback;
    }

    internal static DateTimeOffset ToCliTimestamp(JsonElement message, DateTimeOffset fallback)
    {
        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("ts", out JsonElement timestamp))
        {
            return ToCliTimestampValue(timestamp, fallback);
        }

        return fallback;
    }

    internal static DateTimeOffset ToCliTimestampValue(JsonElement timestamp, DateTimeOffset fallback)
    {
        if (timestamp.ValueKind == JsonValueKind.Number
            && timestamp.TryGetDouble(out double value)
            && double.IsFinite(value)
            && value > 0)
        {
            double resolved = value < MinReasonableTimestampMs ? value * 1000 : value;
            if (double.IsFinite(resolved)
                && resolved >= MinUnixMilliseconds
                && resolved <= MaxUnixMilliseconds)
            {
                DateTimeOffset candidate = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Truncate(resolved)).ToUniversalTime();
                if (candidate.ToUnixTimeMilliseconds() >= MinReasonableTimestampMs)
                {
                    return candidate;
                }
            }
        }

        if (timestamp.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                timestamp.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        return fallback;
    }

    internal static string? ReadModelId(JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("modelInfo", out JsonElement modelInfo)
            && modelInfo.ValueKind == JsonValueKind.Object)
        {
            return ReadText(modelInfo, "id");
        }

        return null;
    }

    internal static string? FirstTextContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return ReadNonEmpty(content.GetString());
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !string.Equals(ReadText(block, "type"), "text", StringComparison.Ordinal))
            {
                continue;
            }

            string? text = ReadNonEmpty(ReadRawBlockText(block, "text"));
            if (text is not null)
            {
                return text;
            }
        }

        return null;
    }

    internal static (List<ClineCliToolCall> Tools, List<string> Skills, List<string> SubagentTypes, int WebSearchRequests) CollectTools(
        JsonElement message,
        int messageIndex)
    {
        List<ClineCliToolCall> tools = [];
        List<string> skills = [];
        List<string> subagentTypes = [];
        int webSearchRequests = 0;

        if (!message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return (tools, skills, subagentTypes, webSearchRequests);
        }

        int toolIndex = 0;
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !string.Equals(ReadText(block, "type"), "tool_use", StringComparison.Ordinal))
            {
                continue;
            }

            string? rawName = ReadText(block, "name");
            if (rawName is null)
            {
                continue;
            }

            string mapped = ToolNameMap.GetValueOrDefault(rawName, rawName);
            JsonElement input = block.TryGetProperty("input", out JsonElement inputElement) ? inputElement : default;
            string callId = ReadText(block, "id")
                ?? string.Create(CultureInfo.InvariantCulture, $"call-{messageIndex.ToString(CultureInfo.InvariantCulture)}-{toolIndex.ToString(CultureInfo.InvariantCulture)}");

            tools.Add(new ClineCliToolCall(callId, mapped, InputJsonOf(input)));
            toolIndex++;

            if (string.Equals(mapped, "Bash", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(mapped, "Skill", StringComparison.Ordinal))
            {
                string? skill = FirstInputString(input, ["name", "skill", "skill_name"]);
                if (skill is not null)
                {
                    skills.Add(skill);
                }

                continue;
            }

            if (string.Equals(mapped, "Agent", StringComparison.Ordinal))
            {
                string? subagentType = FirstInputString(input, ["agent", "agent_type", "type", "name"]);
                if (subagentType is not null)
                {
                    subagentTypes.Add(subagentType);
                }

                continue;
            }

            if (string.Equals(mapped, "WebFetch", StringComparison.Ordinal))
            {
                webSearchRequests++;
            }
        }

        return (tools, skills, subagentTypes, webSearchRequests);
    }

    private static string? ReadRawBlockText(JsonElement block, string propertyName)
    {
        if (block.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static string? ReadNonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FirstInputString(JsonElement input, string[] keys)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string key in keys)
        {
            string? value = ReadText(input, key);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string? InputJsonOf(JsonElement input)
    {
        return input.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => input.GetRawText(),
            JsonValueKind.String => input.GetString(),
            _ => null,
        };
    }

    private static DateTimeOffset FallbackTimestamp(NativeSourceUnit unit, DateTimeOffset importedAtUtc) =>
        unit.LastWriteUtc == default ? importedAtUtc.ToUniversalTime() : unit.LastWriteUtc.ToUniversalTime();

    private CollectedBatch MapTask(
        NativeSourceUnit unit,
        CollectedSource source,
        DateTimeOffset importedAtUtc,
        string harnessId)
    {
        DateTimeOffset fallback = FallbackTimestamp(unit, importedAtUtc);
        ClineTaskDocument document = ClineTaskDocument.Parse(unit.RawText, unit.Unit.SourceKey, fallback);
        List<CollectedProblem> problems = BuildProblems(
            document.ParseProblems,
            unit.Unit.SourceKey,
            harnessId,
            null,
            importedAtUtc);

        if (document.Entries.Count == 0)
        {
            problems.Add(UnrecognizedProblem(unit.Unit.SourceKey, harnessId, importedAtUtc));
            return new CollectedBatch(source, [], [], problems);
        }

        string sessionKey = document.TaskId;
        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);
        for (int index = 0; index < document.Entries.Count; index++)
        {
            ClineUsageEntry entry = document.Entries[index];
            string recordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionKey}:req-{entry.Index.ToString(CultureInfo.InvariantCulture)}"));

            List<CollectedContentPart> parts = [];
            string? correctionRule = null;
            if (index == 0 && !string.IsNullOrWhiteSpace(document.UserMessage))
            {
                CorrectionDetectionResult correction = CanonFindings.DetectUserCorrection(document.UserMessage);
                if (correction.Matched)
                {
                    correctionRule = correction.Rule;
                }

                parts.Add(BuildContentPart(0, ContentBucket.UserMessage, document.UserMessage, "user"));
            }

            records.Add(BuildLlmRecord(
                recordId,
                harnessId,
                sessionKey,
                DefaultTaskModel,
                entry.Timestamp,
                entry.InputTokens,
                entry.OutputTokens,
                entry.CacheReadTokens,
                entry.CacheWriteTokens,
                entry.Cost.HasValue ? new CollectedCost(entry.Cost.Value, "USD") : null,
                parts,
                [],
                correctionRule,
                entry.InputTokens > 0 ? entry.InputTokens : null,
                entry.OutputTokens > 0 ? entry.OutputTokens : null));
        }

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: sessionKey,
                HarnessId: harnessId,
                NativeSessionId: sessionKey,
                Workspace: null,
                Title: null,
                ReportedUsage: []),
        ];

        return new CollectedBatch(source, sessions, records, problems);
    }

    private CollectedBatch MapCli(
        NativeSourceUnit unit,
        CollectedSource source,
        DateTimeOffset importedAtUtc,
        string harnessId)
    {
        DateTimeOffset fallback = FallbackTimestamp(unit, importedAtUtc);
        ClineCliDocument document = ClineCliDocument.Parse(unit.RawText, unit.Unit.SourceKey, fallback);
        List<CollectedProblem> problems = BuildProblems(
            document.ParseProblems,
            unit.Unit.SourceKey,
            harnessId,
            null,
            importedAtUtc);

        if (document.Messages.Count == 0)
        {
            problems.Add(UnrecognizedProblem(unit.Unit.SourceKey, harnessId, importedAtUtc));
            return new CollectedBatch(source, [], [], problems);
        }

        string sessionKey = document.SessionId;
        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);
        for (int index = 0; index < document.Messages.Count; index++)
        {
            ClineCliMessage message = document.Messages[index];
            string recordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionKey}:{message.MessageId}"));

            List<CollectedContentPart> parts = [];
            string? correctionRule = null;
            if (index == 0 && !string.IsNullOrWhiteSpace(document.UserMessage))
            {
                CorrectionDetectionResult correction = CanonFindings.DetectUserCorrection(document.UserMessage);
                if (correction.Matched)
                {
                    correctionRule = correction.Rule;
                }

                parts.Add(BuildContentPart(0, ContentBucket.UserMessage, document.UserMessage, "user"));
            }

            CollectedRecord parent = BuildLlmRecord(
                recordId,
                harnessId,
                sessionKey,
                message.Model,
                message.Timestamp,
                message.InputTokens,
                message.OutputTokens,
                message.CacheReadTokens,
                message.CacheWriteTokens,
                message.CostReported ? new CollectedCost(message.Cost, "USD") : null,
                parts,
                message.Skills,
                correctionRule,
                message.InputTokens > 0 ? message.InputTokens : null,
                message.OutputTokens > 0 ? message.OutputTokens : null);
            records.Add(parent);

            foreach (ClineCliToolCall tool in message.Tools)
            {
                records.Add(BuildToolInvokeRecord(parent, tool, sessionKey, harnessId, seenRecordIds));
            }
        }

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: sessionKey,
                HarnessId: harnessId,
                NativeSessionId: sessionKey,
                Workspace: null,
                Title: null,
                ReportedUsage: []),
        ];

        return new CollectedBatch(source, sessions, records, problems);
    }

    private static CollectedRecord BuildLlmRecord(
        string recordId,
        string harnessId,
        string sessionKey,
        string model,
        DateTimeOffset startedAt,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        CollectedCost? reportedCost,
        List<CollectedContentPart> parts,
        IReadOnlyList<string> skills,
        string? correctionRule,
        long? reportedInputTokens,
        long? reportedOutputTokens)
    {
        List<CollectedTokenCount> tokens = [];
        if (inputTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, inputTokens));
        }

        if (outputTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, outputTokens));
        }

        if (cacheReadTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, cacheReadTokens));
        }

        if (cacheWriteTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, cacheWriteTokens));
        }

        return new CollectedRecord(
            RecordId: recordId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: string.Create(CultureInfo.InvariantCulture, $"{harnessId}:{model}"),
            StartedAt: startedAt,
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: string.Create(
                CultureInfo.InvariantCulture,
                $"{CanonSynth.SynthSpanPrefix}{harnessId}:{sessionKey}"),
            Model: model,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: tokens,
            ReportedCost: reportedCost,
            Parts: parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: skills,
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(
                ErrorMessage: null,
                EditFailures: null,
                CorrectionRule: correctionRule,
                DeclaredContextWindow: null),
            ReportedInputTokens: reportedInputTokens,
            ReportedOutputTokens: reportedOutputTokens);
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parent,
        ClineCliToolCall tool,
        string sessionKey,
        string harnessId,
        HashSet<string> seenRecordIds)
    {
        ToolContentMeasurement? measurement = tool.InputJson is null
            ? null
            : ToolContentMeasurer.Measure(tool.InputJson);
        CollectedToolContent? input = measurement?.ToToolContent(includeFingerprint: true);

        string childRecordId = EnsureUniqueRecordId(
            seenRecordIds,
            string.Create(CultureInfo.InvariantCulture, $"{parent.RecordId}:{tool.CallId}"));

        CollectedToolCall collectedTool = new(
            CallId: tool.CallId,
            ToolName: tool.ToolName,
            McpServerName: null,
            McpToolName: null,
            Input: input,
            Result: null,
            Succeeded: null,
            Error: null,
            ExitCode: null);

        return new CollectedRecord(
            RecordId: childRecordId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: tool.ToolName,
            StartedAt: parent.StartedAt,
            Status: RecordStatus.Unset,
            SessionKey: sessionKey,
            ParentRecordId: parent.RecordId,
            TraceId: parent.TraceId,
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

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate) =>
        HarnessJson.EnsureUniqueRecordIdWithIndexSuffix(seenRecordIds, candidate);

    private static CollectedProblem UnrecognizedProblem(
        string sourceKey,
        string harnessId,
        DateTimeOffset importedAtUtc) =>
        new CollectedProblem(
            ProblemKey: string.Create(CultureInfo.InvariantCulture, $"cline-unrecognized:{sourceKey}"),
            Severity: "warning",
            Code: UnrecognizedTranscriptCode,
            Message: string.Create(
                CultureInfo.InvariantCulture,
                $"Skipping unrecognized {harnessId} session '{sourceKey}': no usage-bearing turns extracted."),
            SpanId: null,
            Location: sourceKey,
            SessionKey: null,
            HarnessId: harnessId,
            TimestampUtc: FormatTimestamp(importedAtUtc),
            Stack: null);

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<ClineParseProblem> parseProblems,
        string sourceKey,
        string harnessId,
        string? sessionKey,
        DateTimeOffset importedAtUtc)
    {
        List<CollectedProblem> problems = new(parseProblems.Count);
        foreach (ClineParseProblem parseProblem in parseProblems)
        {
            string suffix = parseProblem.LineNumber > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $":line-{parseProblem.LineNumber.ToString(CultureInfo.InvariantCulture)}")
                : string.Empty;
            problems.Add(new CollectedProblem(
                ProblemKey: string.Create(
                    CultureInfo.InvariantCulture,
                    $"cline-malformed:{sourceKey}{suffix}"),
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

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
