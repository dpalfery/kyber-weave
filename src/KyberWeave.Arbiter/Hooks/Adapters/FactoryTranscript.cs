using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;

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

    /// <summary>Whether a <c>Task</c> result is the notice of a background launch.</summary>
    /// <param name="text">The result text.</param>
    /// <returns>True when the text opens with the launch notice.</returns>
    public static bool IsBackgroundLaunch(string text) =>
        text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith(BackgroundLaunchPrefix, StringComparison.Ordinal);

    /// <summary>Whether a <c>tool_response</c>, plain or enveloped, is a background launch.</summary>
    /// <param name="response">The hook payload's response.</param>
    /// <returns>True when any string in it opens with the launch notice.</returns>
    public static bool IsBackgroundLaunch(JsonElement response) => response.ValueKind switch
    {
        JsonValueKind.String => IsBackgroundLaunch(response.GetString() ?? string.Empty),
        JsonValueKind.Array => response.EnumerateArray().Any(IsBackgroundLaunch),
        JsonValueKind.Object => response.EnumerateObject().Any(property => IsBackgroundLaunch(property.Value)),
        _ => false,
    };

    /// <summary>
    /// Finds the latest <c>Task</c> dispatch with this description that launched in the
    /// background. A foreground dispatch has no launch result, so it is not found and
    /// its <c>PostToolUse</c> stays the one record of its return. A description is not an
    /// identity: launches that name different tasks cannot be told apart, so none is
    /// returned rather than crediting the wrong task, while a retry of one task is safe.
    /// </summary>
    /// <returns>The dispatch, or null when the transcript cannot supply one.</returns>
    public static FactoryBackgroundDispatch? FindBackgroundDispatch(string transcriptPath, string description)
    {
        ArgumentNullException.ThrowIfNull(transcriptPath);
        ArgumentNullException.ThrowIfNull(description);

        try
        {
            FileInfo file = new(transcriptPath);
            if (!IsTranscriptPath(transcriptPath) || !file.Exists || file.Length > MaxTranscriptBytes)
            {
                return null;
            }

            // Holds only dispatches with this description, not every tool use.
            Dictionary<string, FactoryBackgroundDispatch> dispatches = new(StringComparer.Ordinal);
            List<FactoryBackgroundDispatch> launches = [];
            long remaining = MaxTranscriptBytes;
            foreach (string line in File.ReadLines(transcriptPath))
            {
                // The file may have grown since its length was read.
                remaining -= Encoding.UTF8.GetByteCount(line) + 1;
                if (remaining < 0)
                {
                    return null;
                }

                if (line.Contains("\"Task\"", StringComparison.Ordinal)
                    || (dispatches.Count > 0 && line.Contains("tool_result", StringComparison.Ordinal)))
                {
                    if (ReadLine(line, description, dispatches) is { } launched)
                    {
                        launches.Add(launched);
                    }
                }
            }

            return launches.Select(TaskOf).Distinct().Count() == 1 ? launches[^1] : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsTranscriptPath(string path) =>
        Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase);

    private static (string? Plan, string? Task) TaskOf(FactoryBackgroundDispatch dispatch)
    {
        IReadOnlyDictionary<string, string> headers = HeaderBlock.Parse(dispatch.Prompt).Headers;
        return (headers.GetValueOrDefault("PLAN_FILE"), headers.GetValueOrDefault("TASK"));
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
                    && IsBackgroundLaunch(ResultText(block)))
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
