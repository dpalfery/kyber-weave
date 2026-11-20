using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// Reads the project-scope Arbiter hook a project declares for a harness, so the user-level hook knows
/// whether to defer a dispatch or to answer it itself.
/// </summary>
/// <remarks>
/// Five harnesses have a project hook file this probe reads. Antigravity keeps a <c>PreToolUse</c> matcher
/// group for <c>invoke_subagent</c> in <c>.agents/hooks.json</c>. Factory keeps its event map in
/// <c>.factory/hooks.json</c>, falling back to the <c>hooks</c> key of <c>.factory/settings.json</c> when that
/// file is absent, and dispatches on <c>Task</c>. Cursor keeps a <c>hooks.preToolUse</c> list in
/// <c>.cursor/hooks.json</c>, also dispatching on <c>Task</c>. A JSON harness is covered when a matching
/// entry's command runs the project-scope hook. The JSON files are read as JSONC: a comment or a trailing
/// comma must not hide a hook the file declares. OpenCode keeps the plugin shim at
/// <c>.opencode/plugins/kyber-arbiter.ts</c> and Pi at <c>.pi/extensions/kyber-arbiter.ts</c>. Every other
/// harness reports true, which keeps today's deferral. A missing or malformed file is not a project hook.
/// A file that is present but cannot be read throws, so the hook host logs that failure instead of treating
/// the file as absent.
/// </remarks>
/// <param name="files">The file system the project hook files are read through.</param>
public sealed class ProjectDispatchHookProbe(IFileSystem files) : IProjectDispatchHookProbe
{
    private const string AntigravityToken = "antigravity";
    private const string FactoryToken = "factory";
    private const string CursorToken = "cursor";
    private const string OpenCodeToken = "opencode";
    private const string PiToken = "pi";
    private const string AntigravityHookPath = ".agents/hooks.json";
    private const string FactoryHookPath = ".factory/hooks.json";
    private const string FactorySettingsPath = ".factory/settings.json";
    private const string FactorySettingsHooksKey = "hooks";
    private const string CursorHookPath = ".cursor/hooks.json";
    private const string OpenCodeShimPath = ".opencode/plugins/kyber-arbiter.ts";
    private const string PiShimPath = ".pi/extensions/kyber-arbiter.ts";
    private const string AntigravityDispatchTool = "invoke_subagent";
    private const string FactoryDispatchTool = "Task";
    private const string CursorDispatchTool = "Task";
    private const string AntigravityCommandMarker = "kyber-weave-arbiter hook --harness antigravity";
    private const string FactoryCommandMarker = "kyber-weave-arbiter hook --harness factory";
    private const string CursorCommandMarker = "kyber-weave-arbiter hook --harness cursor";
    private const string GlobalScopeFlag = "--scope global";
    private const string ShimMarker = "\"kyber-weave-arbiter\"";
    private const string ShimGlobalScopeMarker = "\"--scope\", \"global\"";

    // A matcher is a .NET regex in this check. A pattern that stalls must not stall the dispatch.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // A comment or a trailing comma must not hide a declared hook. Factory already tolerates JSONC;
    // Antigravity and Cursor files are read the same way.
    private static readonly JsonDocumentOptions HookJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <inheritdoc />
    public bool CoversHarness(string projectRoot, string harnessToken)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(harnessToken);

        if (string.Equals(harnessToken, AntigravityToken, StringComparison.Ordinal))
        {
            return AntigravityCovers(projectRoot);
        }

        if (string.Equals(harnessToken, FactoryToken, StringComparison.Ordinal))
        {
            return FactoryCovers(projectRoot);
        }

        if (string.Equals(harnessToken, CursorToken, StringComparison.Ordinal))
        {
            return CursorCovers(projectRoot);
        }

        if (string.Equals(harnessToken, OpenCodeToken, StringComparison.Ordinal))
        {
            return ShimCovers(projectRoot, OpenCodeShimPath);
        }

        if (string.Equals(harnessToken, PiToken, StringComparison.Ordinal))
        {
            return ShimCovers(projectRoot, PiShimPath);
        }

        return true;
    }

    private bool AntigravityCovers(string projectRoot)
    {
        if (ReadIfPresent(Path.Combine(projectRoot, AntigravityHookPath)) is not { } text
            || ParseObject(text) is not { } root)
        {
            return false;
        }

        foreach (KeyValuePair<string, JsonNode?> property in root)
        {
            if (property.Value is JsonObject group
                && EventMapCoversDispatchTool(group, AntigravityDispatchTool, AntigravityCommandMarker))
            {
                return true;
            }
        }

        return false;
    }

    private bool FactoryCovers(string projectRoot)
    {
        if (ReadIfPresent(Path.Combine(projectRoot, FactoryHookPath)) is { } hooksText)
        {
            return ParseObject(hooksText) is { } events
                && EventMapCoversDispatchTool(events, FactoryDispatchTool, FactoryCommandMarker);
        }

        // Factory reads the settings hooks key only when hooks.json is absent, so hooks.json wins.
        return ReadIfPresent(Path.Combine(projectRoot, FactorySettingsPath)) is { } settingsText
            && ParseObject(settingsText) is { } settings
            && settings[FactorySettingsHooksKey] is JsonObject fallback
            && EventMapCoversDispatchTool(fallback, FactoryDispatchTool, FactoryCommandMarker);
    }

    private bool CursorCovers(string projectRoot)
    {
        if (ReadIfPresent(Path.Combine(projectRoot, CursorHookPath)) is not { } text
            || ParseObject(text) is not { } root
            || root["hooks"] is not JsonObject hooks
            || hooks["preToolUse"] is not JsonArray entries)
        {
            return false;
        }

        foreach (JsonNode? node in entries)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            // Cursor treats an entry with no matcher as matching every tool.
            string? matcher = Text(entry["matcher"]);
            if (matcher is not null && !Matches(CursorDispatchTool, matcher))
            {
                continue;
            }

            if (Text(entry["command"]) is { } command && IsProjectHook(command, CursorCommandMarker))
            {
                return true;
            }
        }

        return false;
    }

    private bool ShimCovers(string projectRoot, string relativePath)
    {
        if (ReadIfPresent(Path.Combine(projectRoot, relativePath)) is not { } shim)
        {
            return false;
        }

        return shim.Contains(ShimMarker, StringComparison.Ordinal)
            && !shim.Contains(ShimGlobalScopeMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// The file's text, or null when the path is absent. A present file that cannot be read is not absent:
    /// the exception propagates so the hook host logs it.
    /// </summary>
    private string? ReadIfPresent(string path)
    {
        try
        {
            return files.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static JsonObject? ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: HookJsonOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether one harness's event map carries a <c>PreToolUse</c> group for its dispatch tool.</summary>
    private static bool EventMapCoversDispatchTool(JsonObject eventMap, string dispatchTool, string commandMarker)
    {
        if (eventMap["PreToolUse"] is not JsonArray entries)
        {
            return false;
        }

        foreach (JsonNode? node in entries)
        {
            if (node is not JsonObject entry
                || Text(entry["matcher"]) is not { } matcher
                || !Matches(dispatchTool, matcher)
                || entry["hooks"] is not JsonArray actions)
            {
                continue;
            }

            foreach (JsonNode? action in actions)
            {
                if (action is JsonObject hook
                    && Text(hook["command"]) is { } command
                    && IsProjectHook(command, commandMarker))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Matches(string dispatchTool, string matcher)
    {
        try
        {
            return Regex.IsMatch(dispatchTool, matcher, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException)
        {
            // A matcher that does not compile matches nothing.
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsProjectHook(string command, string commandMarker) =>
        command.Contains(commandMarker, StringComparison.Ordinal)
        && !command.Contains(GlobalScopeFlag, StringComparison.Ordinal);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
