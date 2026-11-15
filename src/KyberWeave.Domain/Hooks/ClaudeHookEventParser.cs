using System.Text.Json;

namespace KyberWeave.Domain.Hooks;

/// <summary>Reads the Claude Code PostToolUse hook JSON.</summary>
public static class ClaudeHookEventParser
{
    /// <summary>Reads an edit event from hook JSON.</summary>
    /// <param name="json">The text Claude Code wrote to the hook's stdin.</param>
    /// <returns>The event, or null when the JSON does not name a file in <c>tool_input.file_path</c>.</returns>
    public static HookEditEvent? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_input", out JsonElement input)
                || StringProperty(input, "file_path") is not { } path
                || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return new HookEditEvent(path, StringProperty(root, "cwd") ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
