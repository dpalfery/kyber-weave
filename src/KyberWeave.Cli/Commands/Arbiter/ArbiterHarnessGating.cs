using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>One installed harness and the state its dispatches are gated in for this project.</summary>
/// <param name="Token">The harness token, as <c>--target</c> takes it.</param>
/// <param name="State">The gating state, in words an operator can act on.</param>
/// <param name="FixCommand">The command that gates it, or null when nothing needs fixing.</param>
/// <param name="Ungated">True when no hook gates this harness's dispatches for the project.</param>
internal sealed record HarnessGatingRow(string Token, string State, string? FixCommand, bool Ungated);

/// <summary>The per-harness rows, and the harnesses whose global hook is missing from the global targets.</summary>
/// <param name="Rows">One row per installed harness, in catalogue order.</param>
/// <param name="UnlistedGlobalTokens">Harnesses the global Squad receipt holds an Arbiter hook for that <c>arbiter.global.targets</c> does not list.</param>
internal sealed record HarnessGatingReport(
    IReadOnlyList<HarnessGatingRow> Rows,
    IReadOnlyList<string> UnlistedGlobalTokens);

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
internal static class ArbiterHarnessGating
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

    // These harnesses keep their global hook in a shim file rather than inside a JSON file they share.
    private static readonly IReadOnlySet<SquadTarget> ShimTargets = new HashSet<SquadTarget>
    {
        SquadTarget.Copilot,
        SquadTarget.OpenCode,
        SquadTarget.Kilo,
        SquadTarget.Pi,
    };

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Builds the rows for every harness installed for <paramref name="root"/>.</summary>
    /// <param name="root">The project root whose project hooks and deployments are read.</param>
    /// <param name="home">The home directory whose global targets, receipt and user hooks are read.</param>
    /// <param name="config">The project configuration; an unset <c>arbiter.enabled</c> reads as not configured.</param>
    /// <param name="globalTargets">The harnesses listed under <c>arbiter.global.targets</c>.</param>
    internal static HarnessGatingReport Build(
        string root,
        string home,
        KyberWeaveConfig config,
        IReadOnlyList<string> globalTargets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(globalTargets);

        ISquadGlobalRootResolver roots = SquadCommandComposition.ResolveGlobalRoots(ArbiterCommandComposition.GetEnvironmentVariable);
        SquadStateStore store = SquadCommandComposition.ResolveStateStore(new HomeUserPaths(home));
        SquadReceipt? receipt = ReadGlobalReceipt(store, home);

        List<HarnessGatingRow> rows = [];
        List<string> unlisted = [];
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            string token = SquadTargetCatalog.GetToken(target);
            string? hookPath = ArbiterHookRefresher.ResolveGlobalHookPath(target, home, roots);
            bool listed = globalTargets.Contains(token, StringComparer.Ordinal);
            bool owned = OwnsGlobalHook(receipt, target, token, hookPath, roots);
            bool deployed = HasProjectDeployment(root, target, store, roots);
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

    /// <summary>Prints one line per row, the label first and the fix command after it.</summary>
    /// <param name="report">The rows to print.</param>
    /// <param name="displayName">Turns a harness token into the name the operator knows it by.</param>
    internal static void WriteRows(HarnessGatingReport report, Func<string, string> displayName)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(displayName);

        if (report.Rows.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey]no harness installed for this project.[/]");
            return;
        }

        foreach (HarnessGatingRow row in report.Rows)
        {
            string fix = row.FixCommand is null ? string.Empty : $"; run {row.FixCommand}";
            AnsiConsole.MarkupLine(
                $"  {Markup.Escape(displayName(row.Token))}: {Markup.Escape(row.State)}{Markup.Escape(fix)}");
        }
    }

    private static HarnessGatingRow RowFor(Installed installed, string root, string home, KyberWeaveConfig config)
    {
        string token = installed.Token;
        if (IsFallbackOnly(installed.Target))
        {
            return new HarnessGatingRow(token, FallbackOnlyState, null, Ungated: false);
        }

        if (config.Arbiter.ExplicitlyEnabled == false)
        {
            return new HarnessGatingRow(token, OptedOutState, null, Ungated: false);
        }

        bool live = IsLive(installed);
        if (!config.Arbiter.Enabled)
        {
            return live
                ? new HarnessGatingRow(token, GlobalUserConfigState, null, Ungated: false)
                : new HarnessGatingRow(token, NoProjectConfigState, null, Ungated: false);
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

    private static HarnessGatingRow ClaudeRow(Installed installed, string root, string home, bool live)
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
    private static IReadOnlyList<string> ClaudeProjectSources(string root, string home)
    {
        List<string> sources = [];
        foreach (string? agentType in AgentTypes(root))
        {
            foreach (string eventName in new[] { "PreToolUse", "PostToolUse" })
            {
                sources.AddRange(ClaudeHookCoverage.CandidateSources(root, home, agentType, eventName, AgentToolName));
            }
        }

        return sources.Distinct(StringComparer.Ordinal).ToList();
    }

    // Null stands for a main-thread dispatch. Each project agent file adds its own agent type.
    private static List<string?> AgentTypes(string root)
    {
        List<string?> agentTypes = [null];
        string agents = Path.Combine(root, ".claude", "agents");
        if (!Directory.Exists(agents))
        {
            return agentTypes;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(agents, "*.md"))
            {
                agentTypes.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An agents folder that cannot be listed contributes no agent-file hooks.
        }

        return agentTypes;
    }

    private static bool IsFallbackOnly(SquadTarget target) =>
        ArbiterHookWiring.TargetDegradations([target], SquadDeploymentScope.Project)
            .Any(record => string.Equals(record.Details, FallbackOnlyReason, StringComparison.Ordinal));

    private static bool IsLive(Installed installed)
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
                return File.Exists(installed.HookPath);
            }

            return File.Exists(installed.HookPath)
                && File.ReadAllText(installed.HookPath).Contains(
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
    private static bool IsClaudeUserHookLive(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return false;
        }

        try
        {
            JsonNode? document = JsonNode.Parse(File.ReadAllText(settingsPath));
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

    private static bool OwnsGlobalHook(
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

        string shim = Path.GetFullPath(hookPath);
        return receipt.Files.Any(file =>
            string.Equals(file.Target, token, StringComparison.Ordinal)
            && string.Equals(Path.GetFullPath(Path.Combine(globalRoot, file.RelativePath)), shim, StringComparison.Ordinal));
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

    private static SquadReceipt? ReadGlobalReceipt(SquadStateStore store, string home)
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

    private static bool HasProjectDeployment(string root, SquadTarget target, SquadStateStore store, ISquadGlobalRootResolver roots)
    {
        try
        {
            return ArbiterUninstaller.HasDeployment(root, target, store, SquadDeploymentScope.Project, roots);
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

    /// <summary>
    /// The home-anchored user paths the state store reads global state from. Global Squad state lives
    /// under the home, so the store never reaches the process's own user profile.
    /// </summary>
    private sealed class HomeUserPaths(string home) : ISquadUserPaths
    {
        public string ApplicationDataDirectory => Path.Combine(home, ".kyber-weave");

        public string HomeDirectory => home;
    }
}
