using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Cursor;

/// <summary>
/// Typed representation of one <c>cursorDiskKV</c> bubble row from the Cursor SQLite
/// <c>state.vscdb</c> store, mirroring the <c>BUBBLE_QUERY_BASE</c> projection in
/// <c>dash/src/providers/cursor.ts</c> (<c>bubbleId:&lt;composerId&gt;:&lt;bubbleUuid&gt;</c> keys).
/// </summary>
/// <param name="BubbleKey">SQLite row key (<c>bubbleId:&lt;composerId&gt;:&lt;bubbleUuid&gt;</c>).</param>
/// <param name="InputTokens">Harness-reported input tokens (<c>tokenCount.inputTokens</c>), when present.</param>
/// <param name="OutputTokens">Harness-reported output tokens (<c>tokenCount.outputTokens</c>), when present.</param>
/// <param name="Model">Raw model name (<c>modelInfo.modelName</c>), when present.</param>
/// <param name="CreatedAt">ISO-8601 UTC bubble timestamp (<c>createdAt</c>), when present.</param>
/// <param name="RequestId">Upstream request identifier (<c>requestId</c>), when present.</param>
/// <param name="UserText">Decoded leading bubble text excerpt (<c>text</c>, first 500 characters).</param>
/// <param name="TextLength">Full bubble text length (<c>text_length</c>), when present.</param>
/// <param name="BubbleType">Bubble type (<c>1</c> for user turns, otherwise assistant).</param>
/// <param name="ContextWindow">Harness-reported context window (<c>contextWindow</c>), when present.</param>
/// <param name="CodeBlocks">Decoded <c>codeBlocks</c> JSON array text, when present.</param>
public record CursorBubbleRow(
    [property: JsonPropertyName("bubble_key")] string BubbleKey,
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("request_id")] string? RequestId,
    [property: JsonPropertyName("user_text")] string? UserText,
    [property: JsonPropertyName("text_length")] long? TextLength,
    [property: JsonPropertyName("bubble_type")] long? BubbleType,
    [property: JsonPropertyName("context_window")] long? ContextWindow,
    [property: JsonPropertyName("code_blocks")] string? CodeBlocks);

/// <summary>
/// Non-fatal parse problem encountered while reading a Cursor native surface.
/// </summary>
/// <param name="LineNumber">1-based line or row number within the native payload (0 for document-level failures).</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record CursorParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Parsed <c>cursorDiskKV</c> bubble-row export produced by <see cref="CursorBubbleDocument.Parse"/>.
/// </summary>
/// <param name="Rows">Bubble rows in export order.</param>
/// <param name="ParseProblems">Non-fatal row-level parse diagnostics.</param>
public record CursorBubbleDocument(
    IReadOnlyList<CursorBubbleRow> Rows,
    IReadOnlyList<CursorParseProblem> ParseProblems)
{
    /// <summary>
    /// Parses a JSON array of <see cref="CursorBubbleRow"/> objects mirroring the
    /// <c>BUBBLE_QUERY_BASE</c> projection columns.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the bubble-row export.</param>
    /// <returns>The parsed bubble document.</returns>
    public static CursorBubbleDocument Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new CursorBubbleDocument([], []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException ex)
        {
            return new CursorBubbleDocument([], [Fail(0, $"Skipping malformed Cursor bubble export: {ex.Message}")]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new CursorBubbleDocument(
                    [],
                    [Fail(0, "Skipping Cursor bubble export: expected a top-level JSON array of bubble rows.")]);
            }

            List<CursorBubbleRow> rows = [];
            List<CursorParseProblem> problems = [];
            int index = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                index++;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(Fail(index, $"Skipping non-object Cursor bubble row {index}."));
                    continue;
                }

                CursorBubbleRow? row;
                try
                {
                    row = element.Deserialize<CursorBubbleRow>(BubbleJsonOptions);
                }
                catch (JsonException ex)
                {
                    problems.Add(Fail(index, $"Skipping malformed Cursor bubble row {index}: {ex.Message}"));
                    continue;
                }

                if (row is null || string.IsNullOrWhiteSpace(row.BubbleKey))
                {
                    problems.Add(Fail(index, $"Skipping Cursor bubble row {index} without a bubble key."));
                    continue;
                }

                rows.Add(row);
            }

            return new CursorBubbleDocument(rows, problems);
        }
    }

    private static readonly JsonSerializerOptions BubbleJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static CursorParseProblem Fail(int lineNumber, string message) =>
        new(LineNumber: lineNumber, Code: CursorMapper.MalformedLineCode, Message: message);
}

/// <summary>
/// One parsed <c>cursor-agent</c> transcript turn pairing a user prompt with its assistant reply.
/// </summary>
/// <param name="UserMessage">User prompt text (up to 500 characters).</param>
/// <param name="Body">Assistant reply body text.</param>
/// <param name="Reasoning">Assistant reasoning text (<c>[Thinking]</c> lines on legacy transcripts).</param>
/// <param name="Tools">Unstructured tool names observed on the turn (<c>cursor:&lt;name&gt;</c>).</param>
public record CursorTranscriptTurn(
    string UserMessage,
    string Body,
    string Reasoning,
    IReadOnlyList<string> Tools);

/// <summary>
/// Parsed <c>cursor-agent</c> transcript (<c>agent-transcripts</c> JSONL or legacy text) produced
/// by <see cref="CursorTranscriptDocument.Parse"/>.
/// </summary>
/// <param name="ConversationId">Resolved conversation identifier, when the transcript held any turns.</param>
/// <param name="Turns">Parsed user/assistant turns in transcript order.</param>
/// <param name="Recognized">True when at least one turn was recognized.</param>
/// <param name="ParseProblems">Non-fatal line-level parse diagnostics.</param>
public record CursorTranscriptDocument(
    string? ConversationId,
    IReadOnlyList<CursorTranscriptTurn> Turns,
    bool Recognized,
    IReadOnlyList<CursorParseProblem> ParseProblems)
{
    /// <summary>
    /// Parses a <c>cursor-agent</c> transcript: <c>.jsonl</c> paths use the JSONL turn grammar,
    /// all other paths use the legacy <c>user:</c>/<c>A:</c> marker grammar.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the transcript file.</param>
    /// <param name="transcriptPath">Transcript path; selects the grammar and seeds the conversation id.</param>
    /// <returns>The parsed transcript document.</returns>
    public static CursorTranscriptDocument Parse(string? rawText, string transcriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptPath);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new CursorTranscriptDocument(null, [], false, []);
        }

        bool isJsonl = transcriptPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
        List<CursorTranscriptTurn> turns = [];
        List<CursorParseProblem> problems = [];

        if (isJsonl)
        {
            ParseJsonlTranscript(rawText, turns, problems);
        }
        else
        {
            ParseLegacyTranscript(rawText, turns);
        }

        if (turns.Count == 0)
        {
            return new CursorTranscriptDocument(null, turns, false, problems);
        }

        return new CursorTranscriptDocument(CursorMapper.ToConversationId(transcriptPath), turns, true, problems);
    }

    private static void ParseJsonlTranscript(
        string rawText,
        List<CursorTranscriptTurn> turns,
        List<CursorParseProblem> problems)
    {
        string currentUserMessage = string.Empty;

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

            JsonDocument entryDoc;
            try
            {
                entryDoc = JsonDocument.Parse(rawLine);
            }
            catch (JsonException ex)
            {
                problems.Add(new CursorParseProblem(
                    lineNumber,
                    CursorMapper.MalformedLineCode,
                    $"Skipping malformed cursor-agent JSONL line {lineNumber.ToString(CultureInfo.InvariantCulture)}: {ex.Message}"));
                continue;
            }

            using (entryDoc)
            {
                if (entryDoc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new CursorParseProblem(
                        lineNumber,
                        CursorMapper.MalformedLineCode,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Skipping non-object cursor-agent JSONL line {lineNumber}.")));
                    continue;
                }

                JsonElement entry = entryDoc.RootElement;
                if (!CursorMapper.TryReadText(entry, "role", out string? role) || string.IsNullOrEmpty(role))
                {
                    continue;
                }

                List<JsonElement> blocks = CursorMapper.ReadContentBlocks(entry);
                if (string.Equals(role, "user", StringComparison.Ordinal))
                {
                    List<string> texts = [];
                    foreach (JsonElement block in blocks)
                    {
                        if (CursorMapper.TryReadText(block, "type", out string? blockType)
                            && string.Equals(blockType, "text", StringComparison.Ordinal)
                            && CursorMapper.TryReadText(block, "text", out string? text)
                            && !string.IsNullOrEmpty(text))
                        {
                            texts.Add(text);
                        }
                    }

                    string combined = string.Join(" ", texts);
                    string query = CursorMapper.ExtractUserQuery(combined);
                    currentUserMessage = query.Length > 0
                        ? query
                        : combined.Length <= CursorMapper.MaxUserTextLength
                            ? combined
                            : combined[..CursorMapper.MaxUserTextLength];
                    continue;
                }

                if (!string.Equals(role, "assistant", StringComparison.Ordinal) || currentUserMessage.Length == 0)
                {
                    continue;
                }

                List<string> bodyParts = [];
                List<string> tools = [];
                foreach (JsonElement block in blocks)
                {
                    if (!CursorMapper.TryReadText(block, "type", out string? blockType))
                    {
                        continue;
                    }

                    if (string.Equals(blockType, "text", StringComparison.Ordinal)
                        && CursorMapper.TryReadText(block, "text", out string? text)
                        && !string.IsNullOrEmpty(text))
                    {
                        bodyParts.Add(text);
                    }
                    else if (string.Equals(blockType, "tool_use", StringComparison.Ordinal)
                        && CursorMapper.TryReadText(block, "name", out string? name)
                        && !string.IsNullOrEmpty(name))
                    {
                        tools.Add(string.Create(CultureInfo.InvariantCulture, $"cursor:{CursorMapper.ToLowerAscii(name)}"));
                    }
                }

                turns.Add(new CursorTranscriptTurn(
                    currentUserMessage,
                    string.Join("\n", bodyParts).Trim(),
                    string.Empty,
                    tools));
                currentUserMessage = string.Empty;
            }
        }
    }

    private static void ParseLegacyTranscript(string rawText, List<CursorTranscriptTurn> turns)
    {
        List<string> pendingUsers = [];
        List<string> userLines = [];
        List<string> assistantLines = [];
        CursorSection active = CursorSection.None;

        void FlushUser()
        {
            if (userLines.Count == 0)
            {
                return;
            }

            string userQuery = CursorMapper.ExtractUserQuery(string.Join("\n", userLines));
            if (userQuery.Length > 0)
            {
                pendingUsers.Add(userQuery);
            }

            userLines.Clear();
        }

        void FlushAssistant()
        {
            if (assistantLines.Count == 0)
            {
                return;
            }

            StringBuilder output = new();
            StringBuilder reasoning = new();
            List<string> tools = [];
            foreach (string line in assistantLines)
            {
                if (CursorMapper.ToolResultMarkerRegex().IsMatch(line))
                {
                    continue;
                }

                Match thinkingMatch = CursorMapper.ThinkingMarkerRegex().Match(line);
                if (thinkingMatch.Success)
                {
                    string body = CursorMapper.ThinkingMarkerRegex().Replace(line, string.Empty).Trim();
                    if (body.Length > 0)
                    {
                        _ = reasoning.Append(body).Append('\n');
                    }

                    continue;
                }

                Match toolMatch = CursorMapper.ToolCallMarkerRegex().Match(line);
                if (toolMatch.Success)
                {
                    string parsedTool = CursorMapper.ParseToolName(
                        toolMatch.Groups.Count > 1 ? toolMatch.Groups[1].Value : string.Empty);
                    tools.Add(string.Create(CultureInfo.InvariantCulture, $"cursor:{parsedTool}"));
                    continue;
                }

                _ = output.Append(line).Append('\n');
            }

            if (pendingUsers.Count > 0)
            {
                string userMessage = pendingUsers[0];
                pendingUsers.RemoveAt(0);
                turns.Add(new CursorTranscriptTurn(
                    userMessage,
                    output.ToString().Trim(),
                    reasoning.ToString().Trim(),
                    tools));
            }

            assistantLines.Clear();
        }

        using StringReader reader = new(rawText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (CursorMapper.UserMarkerRegex().IsMatch(line))
            {
                if (active == CursorSection.User)
                {
                    FlushUser();
                }
                else if (active == CursorSection.Assistant)
                {
                    FlushAssistant();
                }

                active = CursorSection.User;
                userLines = [CursorMapper.UserMarkerRegex().Replace(line, string.Empty)];
                continue;
            }

            if (CursorMapper.AssistantMarkerRegex().IsMatch(line))
            {
                if (active == CursorSection.User)
                {
                    FlushUser();
                }
                else if (active == CursorSection.Assistant)
                {
                    FlushAssistant();
                }

                active = CursorSection.Assistant;
                assistantLines = [CursorMapper.AssistantMarkerRegex().Replace(line, string.Empty)];
                continue;
            }

            if (active == CursorSection.User)
            {
                userLines.Add(line);
            }
            else if (active == CursorSection.Assistant)
            {
                assistantLines.Add(line);
            }
        }

        if (active == CursorSection.User)
        {
            FlushUser();
        }
        else if (active == CursorSection.Assistant)
        {
            FlushAssistant();
        }
    }

    private enum CursorSection
    {
        None,
        User,
        Assistant,
    }
}

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting <c>dash/src/providers/cursor.ts</c> (SQLite
/// <c>cursorDiskKV</c> bubble rows) and the <c>cursor-agent</c> transcript grammars into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// Bubble rows arrive as a JSON export mirroring the <c>BUBBLE_QUERY_BASE</c> projection columns;
/// opening SQLite files in unit tests is forbidden (OD-9), so the offline seam parses the exported
/// rows directly. Only the offline-derivable semantics are ported: per-bubble usage (harness-reported
/// counts, estimated from text length when both are zero), composer session attribution, and
/// <c>cursor-agent -&gt; cursor</c> normalization. Live-SQLite-only surfaces (workspace map, composer
/// meter, agent-stream tool attachment, results cache, paged scans) are intentionally absent.
/// </remarks>
public partial class CursorMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for Cursor.</summary>
    public const string CursorHarnessId = "cursor";

    /// <summary>Legacy harness alias folded onto <see cref="CursorHarnessId"/>.</summary>
    public const string CursorAgentHarnessId = "cursor-agent";

    /// <summary>Problem code emitted when a native Cursor line or row cannot be parsed.</summary>
    public const string MalformedLineCode = "CURSOR_MALFORMED_LINE";

    /// <summary>Problem code emitted when a transcript holds no recognizable turns.</summary>
    public const string UnrecognizedTranscriptCode = "CURSOR_UNRECOGNIZED_TRANSCRIPT";

    /// <summary>Maximum user prompt characters retained per turn.</summary>
    internal const int MaxUserTextLength = 500;

    private const int CharsPerToken = 4;
    private const string DefaultBubbleModel = "cursor-auto";
    private const string DefaultTranscriptModel = "cursor-agent-auto";
    private const string UserQueryOpen = "<user_query>";
    private const string UserQueryClose = "</user_query>";

    /// <inheritdoc />
    public string HarnessId => CursorHarnessId;

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

        return IsBubbleExport(unit.RawText)
            ? MapBubbles(unit, source, importedAtUtc, harnessId)
            : MapTranscript(unit, source, importedAtUtc, harnessId);
    }

    /// <summary>
    /// Extracts the composer id from a <c>bubbleId:&lt;composerId&gt;:&lt;bubbleUuid&gt;</c> key.
    /// Returns <see langword="null"/> when the composer segment carries control characters, which
    /// marks Cursor tool-call sub-composer rows rather than standalone composers.
    /// </summary>
    /// <param name="key">Raw bubble row key.</param>
    /// <returns>The composer id, or <see langword="null"/> when the key is not a composer row.</returns>
    internal static string? ParseComposerIdFromKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        int firstColon = key.IndexOf(':', StringComparison.Ordinal);
        if (firstColon < 0)
        {
            return null;
        }

        int secondColon = key.IndexOf(':', firstColon + 1);
        if (secondColon < 0)
        {
            return null;
        }

        string candidate = key.Substring(firstColon + 1, secondColon - firstColon - 1);
        if (candidate.Length == 0)
        {
            return null;
        }

        foreach (char ch in candidate)
        {
            if (ch is '\r' or '\n' or '\0')
            {
                return null;
            }
        }

        return candidate;
    }

    /// <summary>
    /// Resolves the transcript conversation id from its path: UUID file stems are used directly,
    /// all other paths hash to a 16-character digest.
    /// </summary>
    /// <param name="transcriptPath">Transcript source path.</param>
    /// <returns>The conversation identifier.</returns>
    internal static string ToConversationId(string transcriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptPath);
        string stem = TranscriptStem(transcriptPath);
        if (IsUuidStem(stem))
        {
            return stem;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(transcriptPath));
        return Convert.ToHexStringLower(digest)[..16];
    }

    /// <summary>
    /// Extracts <c>&lt;user_query&gt;</c> segments from a user block, falling back to an empty
    /// string when the block carries no query tags.
    /// </summary>
    /// <param name="userBlock">Raw user message text.</param>
    /// <returns>The extracted query trimmed to <see cref="MaxUserTextLength"/> characters.</returns>
    internal static string ExtractUserQuery(string userBlock)
    {
        if (string.IsNullOrEmpty(userBlock))
        {
            return string.Empty;
        }

        StringBuilder chunks = new();
        int cursor = 0;
        while (cursor < userBlock.Length)
        {
            int openIndex = userBlock.IndexOf(UserQueryOpen, cursor, StringComparison.Ordinal);
            if (openIndex < 0)
            {
                break;
            }

            int start = openIndex + UserQueryOpen.Length;
            int closeIndex = userBlock.IndexOf(UserQueryClose, start, StringComparison.Ordinal);
            if (closeIndex < 0)
            {
                _ = chunks.Append(userBlock.Substring(start).Trim());
                break;
            }

            _ = chunks.Append(userBlock.Substring(start, closeIndex - start).Trim()).Append(' ');
            cursor = closeIndex + UserQueryClose.Length;
        }

        string combined = chunks.ToString().Replace("\n", " ", StringComparison.Ordinal).Trim();
        while (combined.Contains("  ", StringComparison.Ordinal))
        {
            combined = combined.Replace("  ", " ", StringComparison.Ordinal);
        }

        return combined.Length <= MaxUserTextLength ? combined : combined[..MaxUserTextLength];
    }

    internal static bool TryReadText(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    internal static List<JsonElement> ReadContentBlocks(JsonElement entry)
    {
        List<JsonElement> blocks = [];
        if (entry.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object)
                {
                    blocks.Add(block);
                }
            }
        }

        return blocks;
    }

    internal static string ParseToolName(string raw)
    {
        string clean = raw.Trim();
        if (clean.Length == 0)
        {
            return "unknown";
        }

        return WhitespaceRunRegex().Replace(ToLowerAscii(clean), "-");
    }

    /// <summary>
    /// Lowercases ASCII letters without consulting the current culture, keeping tool-name slugs
    /// deterministic across locales (and satisfying CA1308, which forbids culture-sensitive
    /// lowercasing for normalized identifiers).
    /// </summary>
    /// <param name="value">Raw tool name.</param>
    /// <returns>The ASCII-lowercased name.</returns>
    internal static string ToLowerAscii(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        char[] buffer = value.ToCharArray();
        for (int index = 0; index < buffer.Length; index++)
        {
            char ch = buffer[index];
            if (ch is >= 'A' and <= 'Z')
            {
                buffer[index] = (char)(ch + ('a' - 'A'));
            }
        }

        return new string(buffer);
    }

    [GeneratedRegex(@"^\s*user:\s*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex UserMarkerRegex();

    [GeneratedRegex(@"^\s*A:\s*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex AssistantMarkerRegex();

    [GeneratedRegex(@"^\s*\[Thinking\]\s*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex ThinkingMarkerRegex();

    [GeneratedRegex(
        @"^\s*\[Tool call\]\s*(.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    internal static partial Regex ToolCallMarkerRegex();

    [GeneratedRegex(
        @"^\s*\[Tool result\]\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    internal static partial Regex ToolResultMarkerRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRunRegex();

    private static string ResolveHarnessId(string? requestedHarnessId)
    {
        string normalized = HarnessSourceRegistry.NormalizeHarnessId(requestedHarnessId ?? string.Empty);
        if (string.Equals(normalized, CursorHarnessId, StringComparison.Ordinal))
        {
            return normalized;
        }

        return CursorHarnessId;
    }

    private static bool IsBubbleExport(string rawText) =>
        rawText.TrimStart().StartsWith('[');

    private static long EstimateTokens(long charCount) =>
        charCount <= 0 ? 0 : (charCount + CharsPerToken - 1) / CharsPerToken;

    private static string TranscriptStem(string transcriptPath)
    {
        string fileName = Path.GetFileName(transcriptPath);
        if (fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".jsonl".Length];
        }

        if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".txt".Length];
        }

        return fileName;
    }

    private static bool IsUuidStem(string stem)
    {
        if (stem.Length != 36 || stem[8] != '-' || stem[13] != '-' || stem[18] != '-' || stem[23] != '-')
        {
            return false;
        }

        foreach (char ch in stem)
        {
            if (ch == '-')
            {
                continue;
            }

            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);

    private CollectedBatch MapBubbles(
        NativeSourceUnit unit,
        CollectedSource source,
        DateTimeOffset importedAtUtc,
        string harnessId)
    {
        CursorBubbleDocument document = CursorBubbleDocument.Parse(unit.RawText);
        List<CollectedProblem> problems = BuildProblems(
            document.ParseProblems,
            unit.Unit.SourceKey,
            harnessId,
            null,
            importedAtUtc);

        Dictionary<string, Queue<string>> userMessages = new(StringComparer.Ordinal);
        foreach (CursorBubbleRow row in document.Rows)
        {
            if (row.BubbleType != 1 || string.IsNullOrWhiteSpace(row.UserText))
            {
                continue;
            }

            string? composerId = ParseComposerIdFromKey(row.BubbleKey);
            if (composerId is null)
            {
                continue;
            }

            if (!userMessages.TryGetValue(composerId, out Queue<string>? queue))
            {
                queue = new Queue<string>();
                userMessages[composerId] = queue;
            }

            queue.Enqueue(row.UserText);
        }

        List<CollectedRecord> records = [];
        Dictionary<string, string> lastUserMessage = new(StringComparer.Ordinal);
        Dictionary<string, List<CollectedRecord>> recordsByComposer = new(StringComparer.Ordinal);
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (CursorBubbleRow row in document.Rows)
        {
            string? composerId = ParseComposerIdFromKey(row.BubbleKey);
            if (composerId is null)
            {
                continue;
            }

            DateTimeOffset? startedAt = TryParseTimestamp(row.CreatedAt);
            if (startedAt is null)
            {
                continue;
            }

            long nativeInput = Math.Max(0, row.InputTokens ?? 0);
            long nativeOutput = Math.Max(0, row.OutputTokens ?? 0);
            long input = nativeInput;
            long output = nativeOutput;
            if (input == 0 && output == 0)
            {
                long textLength = Math.Max(0, row.TextLength ?? 0);
                if (textLength > 0)
                {
                    if (row.BubbleType == 1)
                    {
                        input = EstimateTokens(textLength);
                    }
                    else
                    {
                        output = EstimateTokens(textLength);
                    }
                }

                if (input == 0 && output == 0)
                {
                    continue;
                }
            }

            string pairedUserText;
            if (row.BubbleType == 1)
            {
                pairedUserText = userMessages.TryGetValue(composerId, out Queue<string>? queue) && queue.Count > 0
                    ? queue.Dequeue()
                    : string.Empty;
                lastUserMessage[composerId] = pairedUserText;
            }
            else
            {
                pairedUserText = lastUserMessage.GetValueOrDefault(composerId, string.Empty);
            }

            string displayModel = string.IsNullOrWhiteSpace(row.Model) || string.Equals(row.Model, "default", StringComparison.Ordinal)
                ? DefaultBubbleModel
                : row.Model;
            string anchor = SafeOptionalText(row.RequestId) ?? TailSegment(row.BubbleKey);
            string recordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{harnessId}:{composerId}:{anchor}"));

            List<CollectedContentPart> parts = [];
            int position = 0;
            string? correctionRule = null;
            if (!string.IsNullOrWhiteSpace(pairedUserText))
            {
                CorrectionDetectionResult correction = CanonFindings.DetectUserCorrection(pairedUserText);
                if (correction.Matched)
                {
                    correctionRule = correction.Rule;
                }

                parts.Add(BuildContentPart(position++, ContentBucket.UserMessage, pairedUserText, "user"));
            }

            if (row.BubbleType != 1 && !string.IsNullOrWhiteSpace(row.UserText))
            {
                parts.Add(BuildContentPart(position++, ContentBucket.ConversationHistory, row.UserText, "assistant"));
            }

            records.Add(BuildRecord(
                recordId,
                harnessId,
                composerId,
                string.Create(CultureInfo.InvariantCulture, $"cursor:{displayModel}"),
                startedAt.Value,
                displayModel,
                input,
                output,
                0,
                nativeInput > 0 ? nativeInput : null,
                nativeOutput > 0 ? nativeOutput : null,
                parts,
                correctionRule));

            if (!recordsByComposer.TryGetValue(composerId, out List<CollectedRecord>? composerRecords))
            {
                composerRecords = [];
                recordsByComposer[composerId] = composerRecords;
            }

            composerRecords.Add(records[^1]);
        }

        List<CollectedSession> sessions = [];
        foreach (string composerId in recordsByComposer.Keys)
        {
            sessions.Add(new CollectedSession(
                SessionKey: composerId,
                HarnessId: harnessId,
                NativeSessionId: composerId,
                Workspace: null,
                Title: null,
                ReportedUsage: []));
        }

        return new CollectedBatch(source, sessions, records, problems);
    }

    private CollectedBatch MapTranscript(
        NativeSourceUnit unit,
        CollectedSource source,
        DateTimeOffset importedAtUtc,
        string harnessId)
    {
        CursorTranscriptDocument document = CursorTranscriptDocument.Parse(unit.RawText, unit.Unit.SourceKey);
        List<CollectedProblem> problems = BuildProblems(
            document.ParseProblems,
            unit.Unit.SourceKey,
            harnessId,
            null,
            importedAtUtc);

        if (!document.Recognized || document.Turns.Count == 0 || document.ConversationId is null)
        {
            problems.Add(new CollectedProblem(
                ProblemKey: string.Create(
                    CultureInfo.InvariantCulture,
                    $"cursor-unrecognized:{unit.Unit.SourceKey}"),
                Severity: "warning",
                Code: UnrecognizedTranscriptCode,
                Message: string.Create(
                    CultureInfo.InvariantCulture,
                    $"Skipping unrecognized cursor-agent transcript '{unit.Unit.SourceKey}': no turns extracted."),
                SpanId: null,
                Location: unit.Unit.SourceKey,
                SessionKey: null,
                HarnessId: harnessId,
                TimestampUtc: FormatTimestamp(importedAtUtc),
                Stack: null));
            return new CollectedBatch(source, [], [], problems);
        }

        string conversationId = document.ConversationId;
        DateTimeOffset startedAt = unit.LastWriteUtc == default
            ? importedAtUtc.ToUniversalTime()
            : unit.LastWriteUtc.ToUniversalTime();

        List<CollectedRecord> records = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);
        for (int index = 0; index < document.Turns.Count; index++)
        {
            CursorTranscriptTurn turn = document.Turns[index];
            long input = EstimateTokens(turn.UserMessage.Length);
            long output = EstimateTokens(turn.Body.Length);
            long reasoning = EstimateTokens(turn.Reasoning.Length);
            if (input == 0 && output == 0)
            {
                continue;
            }

            string recordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{CanonSynth.SynthSpanPrefix}{harnessId}:{conversationId}:turn-{index.ToString(CultureInfo.InvariantCulture)}"));

            List<CollectedContentPart> parts = [];
            int position = 0;
            string? correctionRule = null;
            if (!string.IsNullOrWhiteSpace(turn.UserMessage))
            {
                CorrectionDetectionResult correction = CanonFindings.DetectUserCorrection(turn.UserMessage);
                if (correction.Matched)
                {
                    correctionRule = correction.Rule;
                }

                parts.Add(BuildContentPart(position++, ContentBucket.UserMessage, turn.UserMessage, "user"));
            }

            if (!string.IsNullOrWhiteSpace(turn.Body))
            {
                parts.Add(BuildContentPart(position++, ContentBucket.ConversationHistory, turn.Body, "assistant"));
            }

            if (!string.IsNullOrWhiteSpace(turn.Reasoning))
            {
                parts.Add(BuildContentPart(position++, ContentBucket.Reasoning, turn.Reasoning, "assistant"));
            }

            records.Add(BuildRecord(
                recordId,
                harnessId,
                conversationId,
                string.Create(CultureInfo.InvariantCulture, $"cursor:{DefaultTranscriptModel}"),
                startedAt,
                DefaultTranscriptModel,
                input,
                output,
                reasoning,
                null,
                null,
                parts,
                correctionRule));
        }

        if (records.Count == 0)
        {
            return new CollectedBatch(source, [], [], problems);
        }

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: conversationId,
                HarnessId: harnessId,
                NativeSessionId: conversationId,
                Workspace: null,
                Title: null,
                ReportedUsage: []),
        ];

        return new CollectedBatch(source, sessions, records, problems);
    }

    private CollectedRecord BuildRecord(
        string recordId,
        string harnessId,
        string sessionKey,
        string recordName,
        DateTimeOffset startedAt,
        string model,
        long inputTokens,
        long outputTokens,
        long reasoningTokens,
        long? reportedInputTokens,
        long? reportedOutputTokens,
        List<CollectedContentPart> parts,
        string? correctionRule)
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

        if (reasoningTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Reasoning, reasoningTokens));
        }

        return new CollectedRecord(
            RecordId: recordId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: recordName,
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
            ReportedCost: null,
            Parts: parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
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

    private static string TailSegment(string bubbleKey)
    {
        int lastColon = bubbleKey.LastIndexOf(':');
        return lastColon >= 0 && lastColon + 1 < bubbleKey.Length
            ? bubbleKey.Substring(lastColon + 1)
            : bubbleKey;
    }

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<CursorParseProblem> parseProblems,
        string sourceKey,
        string harnessId,
        string? sessionKey,
        DateTimeOffset importedAtUtc)
    {
        List<CollectedProblem> problems = new(parseProblems.Count);
        foreach (CursorParseProblem parseProblem in parseProblems)
        {
            string suffix = parseProblem.LineNumber > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $":line-{parseProblem.LineNumber.ToString(CultureInfo.InvariantCulture)}")
                : string.Empty;
            problems.Add(new CollectedProblem(
                ProblemKey: string.Create(
                    CultureInfo.InvariantCulture,
                    $"cursor-malformed:{sourceKey}{suffix}"),
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
