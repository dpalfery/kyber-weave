using System.Text.Json;

namespace KyberWeave.Domain.Hooks;

/// <summary>Reads the Claude Code PostToolUse hook JSON.</summary>
public static class ClaudeHookEventParser
{
    /// <summary>Gets the most characters of hook JSON the parser accepts; larger input is reported as a problem without being parsed.</summary>
    public const int MaxInputChars = 1_048_576;

    /// <summary>Reads an edit event from hook JSON.</summary>
    /// <param name="json">The text Claude Code wrote to the hook's stdin; null or blank counts as no input.</param>
    /// <returns>
    /// A result with the event when the JSON names a file in <c>tool_input.file_path</c>; a result with neither event nor
    /// problem when the tool input names no file (not an edit); a result with a problem when the input is oversized,
    /// malformed, or not shaped like Claude Code hook JSON.
    /// </returns>
    public static HookParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return HookParseResult.Invalid("the hook received no input on stdin.");
        }

        if (json.Length > MaxInputChars)
        {
            return HookParseResult.Invalid($"the hook input is larger than {MaxInputChars} characters.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException ex)
        {
            return HookParseResult.Invalid($"the hook input is not valid JSON ({ex.Message}).");
        }
    }

    private static HookParseResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return HookParseResult.Invalid("the hook input is not a JSON object; is the hook wired to the right harness?");
        }

        if (!root.TryGetProperty("tool_input", out JsonElement input) || input.ValueKind != JsonValueKind.Object)
        {
            return HookParseResult.Invalid("the hook input has no 'tool_input' object; is the hook wired to the right harness?");
        }

        string cwd = string.Empty;
        if (root.TryGetProperty("cwd", out JsonElement cwdValue))
        {
            if (cwdValue.ValueKind != JsonValueKind.String)
            {
                return HookParseResult.Invalid("the hook input has a 'cwd' that is not a string.");
            }

            cwd = cwdValue.GetString() ?? string.Empty;
        }

        if (!input.TryGetProperty("file_path", out JsonElement pathValue) || pathValue.ValueKind == JsonValueKind.Null)
        {
            return HookParseResult.NotAnEdit;
        }

        if (pathValue.ValueKind != JsonValueKind.String)
        {
            return HookParseResult.Invalid("the hook input has a 'tool_input.file_path' that is not a string.");
        }

        string? path = pathValue.GetString();
        return string.IsNullOrWhiteSpace(path) ? HookParseResult.NotAnEdit : new HookParseResult(new HookEditEvent(path, cwd), null);
    }
}
