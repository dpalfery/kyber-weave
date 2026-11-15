using System.Text.Json;

namespace KyberWeave.Domain.Hooks;

/// <summary>Reads the Claude Code PostToolUse hook JSON.</summary>
public static class ClaudeHookEventParser
{
    /// <summary>Tries to read an edit event from hook JSON.</summary>
    /// <param name="json">The text Claude Code wrote to the hook's stdin.</param>
    /// <param name="edit">The event when parsing succeeds.</param>
    /// <returns>True when the JSON names a file in <c>tool_input.file_path</c>.</returns>
    public static bool TryParse(string json, out HookEditEvent? edit)
    {
        edit = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_input", out JsonElement input)
                || input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("file_path", out JsonElement path)
                || path.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(path.GetString()))
            {
                return false;
            }

            string cwd = root.TryGetProperty("cwd", out JsonElement cwdElement) && cwdElement.ValueKind == JsonValueKind.String
                ? cwdElement.GetString() ?? string.Empty
                : string.Empty;
            string? tool = root.TryGetProperty("tool_name", out JsonElement toolElement) && toolElement.ValueKind == JsonValueKind.String
                ? toolElement.GetString()
                : null;

            edit = new HookEditEvent(path.GetString()!, cwd, tool);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
