using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// Reads the project-scope Arbiter hook a project declares for a harness, so the user-level hook knows
/// whether to defer a dispatch or to answer it itself.
/// </summary>
/// <remarks>
/// Only Antigravity and OpenCode have a project hook file this probe can read. Antigravity keeps its hook in
/// <c>.agents/hooks.json</c>, where a dispatch hook is a <c>PreToolUse</c> matcher group for
/// <c>invoke_subagent</c> running the project-scope command. OpenCode keeps it in the plugin shim at
/// <c>.opencode/plugins/kyber-arbiter.ts</c>. Every other harness reports true, which keeps today's deferral.
/// Nothing here throws: an unreadable or malformed file is a project hook that was not proven.
/// </remarks>
/// <param name="files">The file system the project hook files are read through.</param>
public sealed class ProjectDispatchHookProbe(IFileSystem files) : IProjectDispatchHookProbe
{
    private const string AntigravityToken = "antigravity";
    private const string OpenCodeToken = "opencode";
    private const string AntigravityHookPath = ".agents/hooks.json";
    private const string OpenCodeShimPath = ".opencode/plugins/kyber-arbiter.ts";
    private const string AntigravityDispatchTool = "invoke_subagent";
    private const string AntigravityCommandMarker = "kyber-weave-arbiter hook --harness antigravity";
    private const string GlobalScopeFlag = "--scope global";
    private const string OpenCodeMarker = "\"kyber-weave-arbiter\"";
    private const string OpenCodeGlobalScopeMarker = "\"--scope\", \"global\"";

    // A matcher is a .NET regex in this check. A pattern that stalls must not stall the dispatch.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public bool CoversHarness(string projectRoot, string harnessToken)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(harnessToken);

        if (string.Equals(harnessToken, AntigravityToken, StringComparison.Ordinal))
        {
            return AntigravityCovers(projectRoot);
        }

        if (string.Equals(harnessToken, OpenCodeToken, StringComparison.Ordinal))
        {
            return OpenCodeCovers(projectRoot);
        }

        return true;
    }

    private bool AntigravityCovers(string projectRoot)
    {
        try
        {
            string path = Path.Combine(projectRoot, AntigravityHookPath);
            if (!files.FileExists(path))
            {
                return false;
            }

            if (JsonNode.Parse(files.ReadAllText(path)) is not JsonObject root)
            {
                return false;
            }

            foreach (KeyValuePair<string, JsonNode?> property in root)
            {
                if (property.Value is not JsonObject group || group["PreToolUse"] is not JsonArray entries)
                {
                    continue;
                }

                foreach (JsonNode? node in entries)
                {
                    if (node is not JsonObject entry
                        || Text(entry["matcher"]) is not { } matcher
                        || !Matches(matcher)
                        || entry["hooks"] is not JsonArray actions)
                    {
                        continue;
                    }

                    foreach (JsonNode? action in actions)
                    {
                        if (action is JsonObject hook
                            && Text(hook["command"]) is { } command
                            && IsProjectAntigravityHook(command))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException)
        {
            // A file that cannot be read or parsed proves no project hook.
            return false;
        }
    }

    private bool OpenCodeCovers(string projectRoot)
    {
        try
        {
            string path = Path.Combine(projectRoot, OpenCodeShimPath);
            if (!files.FileExists(path))
            {
                return false;
            }

            string shim = files.ReadAllText(path);
            return shim.Contains(OpenCodeMarker, StringComparison.Ordinal)
                && !shim.Contains(OpenCodeGlobalScopeMarker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            // A shim that cannot be read proves no project hook.
            return false;
        }
    }

    private static bool Matches(string matcher)
    {
        try
        {
            return Regex.IsMatch(AntigravityDispatchTool, matcher, RegexOptions.CultureInvariant, MatchTimeout);
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

    private static bool IsProjectAntigravityHook(string command) =>
        command.Contains(AntigravityCommandMarker, StringComparison.Ordinal)
        && !command.Contains(GlobalScopeFlag, StringComparison.Ordinal);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
