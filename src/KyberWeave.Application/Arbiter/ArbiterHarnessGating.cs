using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Application.Arbiter;

/// <summary>
/// Works out how each installed harness is gated for one project, for <c>arbiter doctor</c> and
/// <c>arbiter status</c>. A harness is installed when the global targets list it, the global Squad
/// receipt owns an Arbiter hook for it, or a project deployment reaches it.
/// </summary>
/// <remarks>
/// Claude is the one harness with a per-dispatch probe, so its project hooks and its user-level hook
/// are read from disk. Every other harness defers to its own project hooks when the project enables
/// Arbiter, so it is gated only by a project deployment. Everything is read from the home the caller
/// names; nothing is written.
/// </remarks>
public sealed class ArbiterHarnessGating : IArbiterHarnessGating
{
    private const string FallbackOnlyReason = "fallback-only";
    private const string FallbackOnlyState = "fallback-only: no hook; the Arbiter MCP server is its only gate";
    private const string OptedOutState = "opted out (arbiter.enabled: false)";
    private const string GlobalUserConfigState = "gated by the global hook under your user configuration";
    private const string ClaudeGlobalState = "gated by the global hook under this project's configuration";
    private const string NoProjectConfigState = "no project Arbiter config; no global hook is live";
    private const string ClaudeUserHookCommand = "kyber-weave-arbiter hook --harness claude --scope global";
    private const string AgentToolName = "Agent";
    private const string InstallCommandPrefix = "kyber-weave arbiter install";
    private const string DispatchHookCommand = "kyber-weave-arbiter hook";

    // These harnesses keep their global hook in a shim file rather than inside a JSON file they share.
    private static readonly IReadOnlySet<SquadTarget> ShimTargets = new HashSet<SquadTarget>
    {
        SquadTarget.Copilot,
        SquadTarget.OpenCode,
        SquadTarget.Kilo,
        SquadTarget.Pi,
        SquadTarget.CommandCode,
    };

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly ISquadStateStore _store;
    private readonly ISquadGlobalRootResolver _roots;
    private readonly IClaudeHookCoverage _claudeHooks;
    private readonly IWritableFileSystem _files;
    private readonly IArbiterUninstaller _uninstaller;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;

    /// <summary>Creates the gating report over the registered Squad state, hook coverage and file system.</summary>
    /// <param name="store">Reads the global and project receipts.</param>
    /// <param name="roots">Resolves each harness's global root.</param>
    /// <param name="claudeHooks">Lists the Claude hook sources a project declares.</param>
    /// <param name="files">Reads the hook files.</param>
    /// <param name="uninstaller">Says whether a project deployment is present.</param>
    /// <param name="paths">Normalizes the two spellings a shim file is compared under.</param>
    /// <param name="environment">Names the directory a relative spelling is resolved against.</param>
    public ArbiterHarnessGating(
        ISquadStateStore store,
        ISquadGlobalRootResolver roots,
        IClaudeHookCoverage claudeHooks,
        IWritableFileSystem files,
        IArbiterUninstaller uninstaller,
        IPathResolver paths,
        IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(claudeHooks);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(uninstaller);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        _store = store;
        _roots = roots;
        _claudeHooks = claudeHooks;
        _files = files;
        _uninstaller = uninstaller;
        _paths = paths;
        _environment = environment;
    }

    /// <inheritdoc />
    public HarnessGatingReport Build(
        string root,
        string home,
        KyberWeaveConfig config,
        IReadOnlyList<string> globalTargets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(globalTargets);

        ISquadGlobalRootResolver roots = _roots;
        ISquadStateStore store = _store;
        SquadReceipt? receipt = ReadGlobalReceipt(store, home);

        List<HarnessGatingRow> rows = [];
        List<string> unlisted = [];
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            string token = SquadTargetCatalog.GetToken(target);
            string? hookPath = ArbiterGlobalHookPaths.Resolve(target, home, roots);
            bool listed = globalTargets.Contains(token, StringComparer.OrdinalIgnoreCase);
            bool owned = OwnsGlobalHook(receipt, target, token, hookPath, roots);
            bool deployed = HasProjectDeployment(root, target);
            if (!listed && !owned && !deployed)
            {
                continue;
            }

            if (owned && !listed && !unlisted.Contains(token, StringComparer.Ordinal))
            {
                unlisted.Add(token);
            }

            Installed installed = new(target, token, listed, owned, deployed, hookPath);
            rows.Add(RowFor(installed, root, home, config));
        }

        return new HarnessGatingReport(rows, unlisted);
    }

    private HarnessGatingRow PromptHookRow(Installed installed, string root, string arbiterState)
    {
        // Prompt insertion is not an Arbiter action: a host with the Arbiter off still installs the dispatch hook
        // so a worker starts with its task prompt. A deployment that predates the hook, or one rendered without it,
        // is the one case the operator has to fix.
        bool installedHook = HasProjectDispatchHook(root, installed.Token);
        string trust = ArbiterHookWiring.TrustSteps.TryGetValue(installed.Target, out string? step)
            ? $" Trust: {step}"
            : string.Empty;
        if (installed.Deployed && !installedHook)
        {
            return new HarnessGatingRow(
                installed.Token,
                $"{arbiterState}; the dispatch hook that expands a task id into the worker's prompt is not installed," +
                $" so a worker would start with only its task id.{trust}",
                "kyber-weave squad update",
                Ungated: true);
        }

        string state = installedHook
            ? $"{arbiterState}; the dispatch hook is installed"
            : arbiterState;
        return new HarnessGatingRow(installed.Token, state + trust, null, Ungated: false);
    }

    /// <summary>
    /// Whether the project deployment carries the dispatch hook that expands a task id into the worker's prompt.
    /// </summary>
    /// <remarks>
    /// The hook is a command line inside whichever file the harness reads: an agent body on Claude and Copilot, a
    /// shared hook file on Cursor, Codex, Factory and Devin, a plugin shim on Pi, Kilo and OpenCode. The receipt names
    /// every file or block Squad owns for the target, so the check reads those and asks whether the command is in
    /// one. A receipt that cannot be read is reported as a missing hook, as every other unreadable install fact is.
    /// </remarks>
    private bool HasProjectDispatchHook(string root, string token)
    {
        try
        {
            SquadReceipt? receipt = _store.ReadReceipt(root, SquadDeploymentScope.Project);
            if (receipt is null)
            {
                return false;
            }

            IEnumerable<string> paths = receipt.Files
                .Where(file => string.Equals(file.Target, token, StringComparison.Ordinal))
                .Select(file => file.RelativePath)
                .Concat(receipt.Blocks
                    .Where(block => string.Equals(block.Target, token, StringComparison.Ordinal))
                    .Select(block => block.RelativePath));
            foreach (string relative in paths.Distinct(StringComparer.Ordinal))
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (_files.FileExists(path)
                    && _files.ReadAllText(path).Contains(DispatchHookCommand, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    private HarnessGatingRow RowFor(Installed installed, string root, string home, KyberWeaveConfig config)
    {
        string token = installed.Token;
        if (IsFallbackOnly(installed.Target))
        {
            return new HarnessGatingRow(token, FallbackOnlyState, null, Ungated: false);
        }

        if (config.Arbiter.ExplicitlyEnabled == false)
        {
            return PromptHookRow(installed, root, OptedOutState);
        }

        bool live = IsLive(installed);
        if (!config.Arbiter.Enabled)
        {
            return PromptHookRow(
                installed,
                root,
                live ? GlobalUserConfigState : NoProjectConfigState);
        }

        if (installed.Target == SquadTarget.Claude)
        {
            return ClaudeRow(installed, root, home, live);
        }

        if (installed.Deployed)
        {
            return new HarnessGatingRow(token, "gated by project hooks", null, Ungated: false);
        }

        // A harness whose global hook the receipt owns but the targets do not list is not refreshed by
        // update, so the fix is to list it again rather than to add a project deployment.
        if (installed.Owned && !installed.Listed)
        {
            return new HarnessGatingRow(
                token,
                "ungated; its global hook is not in arbiter.global.targets",
                $"{InstallCommandPrefix} --target {token}",
                Ungated: true);
        }

        return new HarnessGatingRow(
            token,
            "ungated",
            $"{InstallCommandPrefix} --project --target {token}",
            Ungated: true);
    }

    private HarnessGatingRow ClaudeRow(Installed installed, string root, string home, bool live)
    {
        string token = installed.Token;
        IReadOnlyList<string> sources = ClaudeProjectSources(root, home);
        if (live)
        {
            string state = sources.Count == 0
                ? ClaudeGlobalState
                : $"{ClaudeGlobalState}; project hooks gate {string.Join(", ", sources)} when they fire";
            return new HarnessGatingRow(token, state, null, Ungated: false);
        }

        if (sources.Count > 0)
        {
            return new HarnessGatingRow(token, $"gated by project hooks: {string.Join(", ", sources)}", null, Ungated: false);
        }

        return new HarnessGatingRow(token, "ungated", $"{InstallCommandPrefix} --target claude", Ungated: true);
    }

    /// <summary>
    /// The project hook sources that would fire for a dispatch: the project settings, each project agent
    /// file's own hooks for its agent type, and the effective skills. Both events are listed, since a
    /// hook on either one gates a dispatch.
    /// </summary>
    private IReadOnlyList<string> ClaudeProjectSources(string root, string home)
    {
        List<string> sources = [];
        foreach (string eventName in new[] { "PreToolUse", "PostToolUse" })
        {
            sources.AddRange(_claudeHooks.ProjectCandidateSources(root, home, eventName, AgentToolName));
        }

        return sources.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsFallbackOnly(SquadTarget target) =>
        ArbiterHookWiring.TargetDegradations([target], SquadDeploymentScope.Project)
            .Any(record => string.Equals(record.Details, FallbackOnlyReason, StringComparison.Ordinal));

    private bool IsLive(Installed installed)
    {
        if (installed.HookPath is null)
        {
            return false;
        }

        try
        {
            if (installed.Target == SquadTarget.Claude)
            {
                return IsClaudeUserHookLive(installed.HookPath);
            }

            if (ShimTargets.Contains(installed.Target))
            {
                return _files.FileExists(installed.HookPath);
            }

            return _files.FileExists(installed.HookPath)
                && _files.ReadAllText(installed.HookPath).Contains(
                    $"kyber-weave-arbiter hook --harness {installed.Token} --scope global",
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Claude reads hooks under the exact container name, so a user hook under <c>preToolUse</c> does not
    /// gate anything. Live means a <c>PreToolUse</c> entry whose matcher matches <c>Agent</c> runs the
    /// user-level hook.
    /// </summary>
    private bool IsClaudeUserHookLive(string settingsPath)
    {
        if (!_files.FileExists(settingsPath))
        {
            return false;
        }

        try
        {
            JsonNode? document = JsonNode.Parse(_files.ReadAllText(settingsPath));
            if (document is not JsonObject root
                || root["hooks"] is not JsonObject hooks
                || hooks["PreToolUse"] is not JsonArray groups)
            {
                return false;
            }

            foreach (JsonNode? node in groups)
            {
                if (node is not JsonObject group || !Matches(Text(group["matcher"]), AgentToolName))
                {
                    continue;
                }

                if (group["hooks"] is not JsonArray actions)
                {
                    continue;
                }

                foreach (JsonNode? action in actions)
                {
                    if (action is JsonObject entry
                        && Text(entry["command"]) is { } command
                        && command.Contains(ClaudeUserHookCommand, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool Matches(string? matcher, string toolName)
    {
        try
        {
            return Regex.IsMatch(toolName, matcher ?? string.Empty, RegexOptions.CultureInvariant, MatchTimeout);
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

    private bool OwnsGlobalHook(
        SquadReceipt? receipt,
        SquadTarget target,
        string token,
        string? hookPath,
        ISquadGlobalRootResolver roots)
    {
        if (receipt is null)
        {
            return false;
        }

        if (receipt.Blocks.Any(block => string.Equals(block.Target, token, StringComparison.Ordinal)))
        {
            return true;
        }

        if (!ShimTargets.Contains(target) || hookPath is null)
        {
            return false;
        }

        string? globalRoot = ResolveGlobalRootOrNull(roots, target);
        if (globalRoot is null)
        {
            return false;
        }

        string shim = _paths.GetFullPath(hookPath, _environment.CurrentDirectory);
        return receipt.Files.Any(file =>
            string.Equals(file.Target, token, StringComparison.Ordinal)
            && _paths.AreSame(
                _paths.GetFullPath(Path.Combine(globalRoot, file.RelativePath), _environment.CurrentDirectory),
                shim));
    }

    private static string? ResolveGlobalRootOrNull(ISquadGlobalRootResolver roots, SquadTarget target)
    {
        try
        {
            return roots.ResolveGlobalRoot(target);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private SquadReceipt? ReadGlobalReceipt(ISquadStateStore store, string home)
    {
        try
        {
            return store.ReadReceipt(home, SquadDeploymentScope.Global);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            // An unreadable receipt cannot show what the global install owns, so no global row is drawn from it.
            return null;
        }
    }

    private bool HasProjectDeployment(string root, SquadTarget target)
    {
        try
        {
            return _uninstaller.HasDeployment(root, target, SquadDeploymentScope.Project);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            // A project deployment that cannot be read is reported as absent, as it was before this check.
            return false;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>One harness that is installed, with what the install left behind.</summary>
    private sealed record Installed(
        SquadTarget Target,
        string Token,
        bool Listed,
        bool Owned,
        bool Deployed,
        string? HookPath);
}
