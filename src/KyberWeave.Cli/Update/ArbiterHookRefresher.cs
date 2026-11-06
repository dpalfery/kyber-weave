using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Cli.Update;

/// <summary>
/// Default arbiter-hook refresh: re-renders hooks for each enabled project through the Squad
/// update path, in-process. A project whose deployment is missing or unreadable is skipped with a
/// warning; the update that already committed its binaries keeps its exit code.
/// </summary>
internal sealed class ArbiterHookRefresher : IArbiterHookRefresher
{
    private readonly Action<string>? _log;
    private readonly Func<string, string?>? _environmentReader;

    internal ArbiterHookRefresher(Action<string>? log = null, Func<string, string?>? environmentReader = null)
    {
        _log = log;
        _environmentReader = environmentReader;
    }

    /// <inheritdoc />
    public ArbiterHookRefreshResult Refresh(IReadOnlyList<string> projectRoots)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);

        SquadLifecycleService lifecycle =
            SquadCommandComposition.CreateLifecycleService(environmentReader: _environmentReader);
        int refreshed = 0;
        int failed = 0;
        foreach (string root in projectRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            KyberWeaveConfigLoadResult loaded;
            try
            {
                loaded = KyberWeaveConfigLoader.TryLoad(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"warning: skipping arbiter hook refresh for '{root}': {ex.Message}");
                failed++;
                continue;
            }

            if (!loaded.Success || loaded.Config?.Arbiter.Enabled != true)
                continue;

            // The refresh already reaches every enabled project, so it is the one place an update
            // can name a project's leftover MCP server key without a second walk of the tree. The
            // line is per project because that is what the operator has to act on, and nothing is
            // written: the rename belongs to `docs mcp-migrate`.
            ReportRetiredMcpKeys(root);

            try
            {
                SquadArbiterWiring wiring = SquadCommandComposition.ResolveArbiterWiring(loaded.Config.Arbiter);
                SquadUpdateRequest request = new(root, SquadDeploymentScope.Project, Arbiter: wiring);
                SquadLifecycleResult result = lifecycle.UpdateAsync(request).GetAwaiter().GetResult();
                if (!result.Success)
                {
                    string reason = result.Errors is { Count: > 0 }
                        ? string.Join(Environment.NewLine, result.Errors)
                        : "the Squad update reported failure.";
                    _log?.Invoke($"warning: arbiter hook refresh for '{root}' failed: {reason}");
                    failed++;
                }
                else
                {
                    refreshed++;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException
                or TimeoutException
                or OperationCanceledException
                or InvalidDataException)
            {
                _log?.Invoke($"warning: arbiter hook refresh for '{root}' failed: {ex.Message}");
                failed++;
            }
        }

        int globalRefreshed = 0;
        int globalFailed = 0;
        foreach ((string? targetToken, string? hookPath) in ResolveGlobalHookTargets())
        {
            if (string.IsNullOrWhiteSpace(hookPath))
                continue;

            if (!File.Exists(hookPath))
                continue;

            try
            {
                if (RefreshGlobalHook(hookPath, targetToken))
                {
                    globalRefreshed++;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                _log?.Invoke($"warning: arbiter global hook refresh for '{targetToken}' failed: {ex.Message}");
                globalFailed++;
            }
        }

        return new ArbiterHookRefreshResult(refreshed + globalRefreshed, failed + globalFailed);
    }

    private IEnumerable<(string TargetToken, string HookPath)> ResolveGlobalHookTargets()
    {
        string? home = _environmentReader is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _environmentReader("HOME");

        if (string.IsNullOrWhiteSpace(home))
            yield break;

        foreach (string token in UserHostConfig.ReadGlobalTargets(home))
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
                continue;

            string? hookPath = ResolveGlobalHookPath(target, home);
            if (!string.IsNullOrWhiteSpace(hookPath))
                yield return (SquadTargetCatalog.GetToken(target), hookPath);
        }
    }

    private static string? ResolveGlobalHookPath(SquadTarget target, string home)
    {
        SquadGlobalRoots roots = new(_ => null, home);
        string root = roots.ResolveGlobalRoot(target);
        return target switch
        {
            SquadTarget.Claude => Path.Combine(root, "settings.json"),
            SquadTarget.Cursor => Path.Combine(root, "hooks.json"),
            SquadTarget.Codex => Path.Combine(root, "hooks.json"),
            SquadTarget.Factory => Path.Combine(root, "hooks.json"),
            SquadTarget.Devin => Path.Combine(root, "hooks.v1.json"),
            SquadTarget.Antigravity => Path.Combine(root, "hooks.json"),
            _ => null,
        };
    }

    private static bool RefreshGlobalHook(string hookPath, string? targetToken)
    {
        if (string.IsNullOrWhiteSpace(targetToken))
            return false;

        string before = File.ReadAllText(hookPath);
        if (string.IsNullOrWhiteSpace(before))
            return false;

        JsonNode? parsed = JsonNode.Parse(before);
        if (parsed is not JsonObject root)
            return false;

        bool changed = false;
        RewriteManagedCommand(root, targetToken, ref changed);
        if (!changed)
            return false;

        string rendered = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(hookPath, rendered);
        return true;
    }

    private static void RewriteManagedCommand(JsonNode? node, string targetToken, ref bool changed)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                if (string.Equals(key, "command", StringComparison.Ordinal)
                    && value is JsonValue commandValue
                    && commandValue.TryGetValue<string>(out string? command)
                    && IsManagedGlobalHookCommand(command, targetToken))
                {
                    obj[key] = ArbiterHookWiring.HookCommandLine(targetToken, SquadDeploymentScope.Global);
                    changed = true;
                    continue;
                }

                RewriteManagedCommand(value, targetToken, ref changed);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                RewriteManagedCommand(child, targetToken, ref changed);
            }
        }
    }

    private static bool IsManagedGlobalHookCommand(string command, string targetToken)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        const string hookSignature = "kyber-weave-arbiter hook";
        int hookIndex = command.IndexOf(hookSignature, StringComparison.Ordinal);
        if (hookIndex < 0)
            return false;

        string remainder = command[(hookIndex + hookSignature.Length)..];
        const string harnessSignature = "--harness ";
        int harnessIndex = remainder.IndexOf(harnessSignature, StringComparison.Ordinal);
        if (harnessIndex < 0)
            return false;

        string harnessAndRemainder = remainder[(harnessIndex + harnessSignature.Length)..];
        if (!harnessAndRemainder.StartsWith(targetToken, StringComparison.Ordinal))
            return false;

        string afterHarness = harnessAndRemainder[targetToken.Length..];
        return afterHarness.TrimStart().StartsWith("--scope global", StringComparison.Ordinal);
    }

    /// <summary>
    /// Names the project's leftover MCP server keys once, in the same log the refresh already writes
    /// to. Read only.
    /// </summary>
    /// <remarks>
    /// A note for the whole run would not say which project holds the leftover, and the operator
    /// would have to search every tree for it. It is a warning because the configuration still works;
    /// it is the name that is stale, not the install.
    /// </remarks>
    private void ReportRetiredMcpKeys(string root)
    {
        IReadOnlyList<McpRenameChange> leftovers =
        [
            .. McpServerRenamer.Scan(root)
                .Where(change => string.Equals(change.Code, McpServerRenamer.RenameCode, StringComparison.Ordinal))
        ];

        foreach (McpRenameChange leftover in leftovers)
        {
            _log?.Invoke(
                $"warning: '{root}' still holds the retired MCP server key '{leftover.OldKey}' "
                + $"({leftover.Path}): {McpServerRenamer.RenameCode}; run {McpServerRenamer.MigrateCommand} "
                + $"--target {leftover.Harness} to rename it to '{leftover.NewKey}'.");
        }
    }
}
