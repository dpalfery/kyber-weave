using System.Text.Json;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>The dispatch a Factory background task came from.</summary>
/// <param name="Target">The dispatched <c>subagent_type</c>.</param>
/// <param name="Prompt">The dispatch prompt, as the parent session recorded it.</param>
internal sealed record FactoryBackgroundDispatch(string Target, string Prompt);

/// <summary>
/// Reads a parent session's Factory transcript. <c>SubagentStop</c> names a task only by
/// its description, so this is where the target and prompt of its dispatch come from.
/// </summary>
internal static class FactoryTranscript
{
    /// <summary>The first line of a <c>Task</c> result that launched in the background.</summary>
    public const string BackgroundLaunchPrefix = "Task launched in background.";

    private const long MaxTranscriptBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Finds the latest <c>Task</c> dispatch with this description that launched in the
    /// background. A foreground dispatch has no launch result, so it is not found and
    /// its <c>PostToolUse</c> stays the one record of its return.
    /// </summary>
    /// <returns>The dispatch, or null when the transcript cannot supply one.</returns>
    public static FactoryBackgroundDispatch? FindBackgroundDispatch(string transcriptPath, string description)
    {
        ArgumentNullException.ThrowIfNull(transcriptPath);
        ArgumentNullException.ThrowIfNull(description);

        try
        {
            FileInfo file = new(transcriptPath);
            if (!file.Exists || file.Length > MaxTranscriptBytes)
            {
                return null;
            }

            Dictionary<string, FactoryBackgroundDispatch> dispatches = new(StringComparer.Ordinal);
            FactoryBackgroundDispatch? latest = null;
            foreach (string line in File.ReadLines(transcriptPath))
            {
                if (line.Contains("tool_use", StringComparison.Ordinal)
                    || line.Contains("tool_result", StringComparison.Ordinal))
                {
                    latest = ReadLine(line, description, dispatches) ?? latest;
                }
            }

            return latest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static FactoryBackgroundDispatch? ReadLine(
        string line,
        string description,
        Dictionary<string, FactoryBackgroundDispatch> dispatches)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("message", out JsonElement message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            FactoryBackgroundDispatch? launched = null;
            foreach (JsonElement block in content.EnumerateArray())
            {
                string? type = GetString(block, "type");
                if (string.Equals(type, "tool_use", StringComparison.Ordinal))
                {
                    RecordDispatch(block, description, dispatches);
                }
                else if (string.Equals(type, "tool_result", StringComparison.Ordinal)
                    && GetString(block, "tool_use_id") is { } id
                    && dispatches.TryGetValue(id, out FactoryBackgroundDispatch? dispatch)
                    && ResultText(block).StartsWith(BackgroundLaunchPrefix, StringComparison.Ordinal))
                {
                    launched = dispatch;
                }
            }

            return launched;
        }
        catch (JsonException)
        {
            // A session still being written can end on a partial line.
            return null;
        }
    }

    private static void RecordDispatch(
        JsonElement block,
        string description,
        Dictionary<string, FactoryBackgroundDispatch> dispatches)
    {
        if (!string.Equals(GetString(block, "name"), "Task", StringComparison.Ordinal)
            || GetString(block, "id") is not { } id
            || !block.TryGetProperty("input", out JsonElement input)
            || !string.Equals(GetString(input, "description"), description, StringComparison.Ordinal)
            || GetString(input, "subagent_type") is not { Length: > 0 } target
            || GetString(input, "prompt") is not { } prompt)
        {
            return;
        }

        dispatches[id] = new FactoryBackgroundDispatch(target, prompt);
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out JsonElement content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        return content.ValueKind == JsonValueKind.Array
            ? string.Concat(content.EnumerateArray().Select(item => GetString(item, "text") ?? string.Empty))
            : string.Empty;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
