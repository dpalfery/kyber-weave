using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.FileSystem;
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

        string? home = ResolveHome();
        int globalRefreshed = 0;
        int globalFailed = 0;
        if (!string.IsNullOrWhiteSpace(home))
        {
            int globalTimeout = ResolveGlobalTimeout(home);
            SquadStateStore stateStore = SquadCommandComposition.ResolveStateStore(
                new SquadCommandComposition.CliSquadUserPaths(home, Path.Combine(home, ".config")),
                _environmentReader);
            SquadReceipt? receipt = null;
            try
            {
                receipt = stateStore.ReadReceipt(home, SquadDeploymentScope.Global);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"warning: could not read global squad receipt: {ex.Message}");
            }

            bool receiptChanged = false;
            List<SquadOwnedFile>? updatedFiles = receipt is not null ? new(receipt.Files) : null;
            List<SquadOwnedBlock>? updatedBlocks = receipt is not null ? new(receipt.Blocks) : null;

            foreach ((string? targetToken, string? hookPath) in ResolveGlobalHookTargets(home))
            {
                if (string.IsNullOrWhiteSpace(hookPath))
                    continue;

                if (!File.Exists(hookPath))
                    continue;

                try
                {
                    if (RefreshGlobalHook(hookPath, targetToken, globalTimeout, out string? newContent, out IReadOnlyList<SquadHookOwnedEntry>? newOwnedEntries))
                    {
                        globalRefreshed++;

                        if (receipt is not null && !string.IsNullOrWhiteSpace(targetToken))
                        {
                            if (newOwnedEntries is { Count: > 0 } && updatedBlocks is not null)
                            {
                                int blockIdx = updatedBlocks.FindIndex(b => string.Equals(b.Target, targetToken, StringComparison.OrdinalIgnoreCase));
                                if (blockIdx >= 0)
                                {
                                    SquadOwnedBlock existingBlock = updatedBlocks[blockIdx];
                                    List<SquadOwnedBlockEntry> blockEntries = newOwnedEntries
                                        .Select(e => new SquadOwnedBlockEntry(e.Location, e.Digest))
                                        .ToList();
                                    updatedBlocks[blockIdx] = existingBlock with { Entries = blockEntries };
                                    receiptChanged = true;
                                }
                            }
                            else if (!string.IsNullOrEmpty(newContent) && updatedFiles is not null)
                            {
                                int fileIdx = updatedFiles.FindIndex(f => string.Equals(f.Target, targetToken, StringComparison.OrdinalIgnoreCase));
                                if (fileIdx >= 0)
                                {
                                    SquadOwnedFile existingFile = updatedFiles[fileIdx];
                                    string newSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(newContent)));
                                    updatedFiles[fileIdx] = existingFile with { Sha256 = newSha };
                                    receiptChanged = true;
                                }
                            }
                        }
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

            if (receipt is not null && receiptChanged)
            {
                try
                {
                    SquadReceipt updatedReceipt = receipt with
                    {
                        Files = updatedFiles ?? receipt.Files,
                        Blocks = updatedBlocks ?? receipt.Blocks
                    };
                    string receiptPath = stateStore.ResolveReceiptPath(home, SquadDeploymentScope.Global);
                    string serialized = stateStore.SerializeReceipt(updatedReceipt);
                    AtomicFile.WriteAllText(receiptPath, serialized);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _log?.Invoke($"warning: updating global squad receipt failed: {ex.Message}");
                }
            }
        }

        return new ArbiterHookRefreshResult(refreshed + globalRefreshed, failed + globalFailed);
    }

    private string? ResolveHome()
    {
        string? home = _environmentReader?.Invoke("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = _environmentReader?.Invoke("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    private static int ResolveGlobalTimeout(string home)
    {
        try
        {
            KyberWeaveConfigLoadResult userConfig = KyberWeaveConfigLoader.TryLoadUserConfig(home);
            if (userConfig.Success && userConfig.Config?.Arbiter is { } arbiter)
            {
                return SquadCommandComposition.ResolveArbiterWiring(arbiter).HookTimeoutSeconds;
            }
        }
        catch
        {
            // Fall back to default.
        }

        return SquadCommandComposition.ResolveArbiterWiring(ArbiterConfig.ProductDefaults).HookTimeoutSeconds;
    }

    private IEnumerable<(string TargetToken, string HookPath)> ResolveGlobalHookTargets(string home)
    {
        foreach (string token in UserHostConfig.ReadGlobalTargets(home))
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
                continue;

            string? hookPath = ResolveGlobalHookPath(target, home, environmentReader: _environmentReader);
            if (!string.IsNullOrWhiteSpace(hookPath))
                yield return (SquadTargetCatalog.GetToken(target), hookPath);
        }
    }

    internal static string? ResolveGlobalHookPath(
        SquadTarget target,
        string home,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string, string?>? environmentReader = null)
    {
        ISquadGlobalRootResolver roots = globalRoots ?? new SquadGlobalRoots(environmentReader ?? (_ => null), home);
        string root;
        try
        {
            root = roots.ResolveGlobalRoot(target);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return target switch
        {
            SquadTarget.Claude => Path.Combine(root, "settings.json"),
            SquadTarget.Cursor => Path.Combine(root, "hooks.json"),
            SquadTarget.Codex => Path.Combine(root, "hooks.json"),
            SquadTarget.Factory => Path.Combine(root, "hooks.json"),
            SquadTarget.Devin => Path.Combine(root, "hooks.v1.json"),
            SquadTarget.Antigravity => Path.Combine(root, "hooks.json"),
            SquadTarget.Kilo => Path.Combine(root, "plugin", "kyber-arbiter.ts"),
            SquadTarget.OpenCode => Path.Combine(root, "plugins", "kyber-arbiter.ts"),
            SquadTarget.Pi => Path.Combine(root, "extensions", "kyber-arbiter.ts"),
            SquadTarget.Copilot => Path.Combine(root, "hooks", "kyber-arbiter.json"),
            _ => null,
        };
    }

    private static bool RefreshGlobalHook(
        string hookPath,
        string? targetToken,
        int timeoutSeconds,
        out string? newContent,
        out IReadOnlyList<SquadHookOwnedEntry>? newOwnedEntries)
    {
        newContent = null;
        newOwnedEntries = null;

        if (string.IsNullOrWhiteSpace(targetToken))
            return false;

        if (!SquadTargetCatalog.TryResolve(targetToken, out SquadTarget target))
            return false;

        if (target is SquadTarget.Kilo or SquadTarget.OpenCode or SquadTarget.Pi or SquadTarget.Copilot)
        {
            string rendered = target switch
            {
                SquadTarget.Kilo => ArbiterPluginShim.Render("kilo", SquadDeploymentScope.Global),
                SquadTarget.OpenCode => ArbiterPluginShim.Render("opencode", SquadDeploymentScope.Global),
                SquadTarget.Pi => PiRenderer.RenderArbiterExtension(timeoutSeconds, SquadDeploymentScope.Global),
                SquadTarget.Copilot => Encoding.UTF8.GetString(
                    CopilotRenderer.RenderCliHooksFile(timeoutSeconds, SquadDeploymentScope.Global, "hooks/kyber-arbiter.json").Content.Span),
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(rendered))
                return false;

            string before = File.Exists(hookPath) ? File.ReadAllText(hookPath) : string.Empty;
            if (string.Equals(before, rendered, StringComparison.Ordinal))
                return false;

            AtomicFile.WriteAllText(hookPath, rendered);
            newContent = rendered;
            return true;
        }

        if (!TryBuildGlobalHookEntries(target, timeoutSeconds, out SquadHookBlockFormat format, out JsonNode[] preToolUse, out JsonNode[] postToolUse))
            return false;

        string existing = File.Exists(hookPath) ? File.ReadAllText(hookPath) : string.Empty;
        if (string.IsNullOrWhiteSpace(existing))
            return false;

        // Factory's global file also carries the SubagentStop group. Splicing without it would
        // strip the registration the install wrote, so the refresh writes it again.
        IReadOnlyList<JsonNode> subagentStop = format == SquadHookBlockFormat.Factory
            ? [CreateMatcherGroup(null, ArbiterHookWiring.HookCommandLine(SquadTarget.Factory, SquadDeploymentScope.Global), timeoutSeconds)]
            : [];
        (string spliced, IReadOnlyList<SquadHookOwnedEntry> owned) = SquadHookJsonBlock.SpliceContent(format, existing, preToolUse, postToolUse, subagentStop);
        if (string.Equals(existing, spliced, StringComparison.Ordinal))
            return false;

        AtomicFile.WriteAllText(hookPath, spliced);
        newContent = spliced;
        newOwnedEntries = owned;
        return true;
    }

    private static bool TryBuildGlobalHookEntries(
        SquadTarget target,
        int timeoutSeconds,
        out SquadHookBlockFormat format,
        out JsonNode[] preToolUse,
        out JsonNode[] postToolUse)
    {
        string commandLine = ArbiterHookWiring.HookCommandLine(target, SquadDeploymentScope.Global);
        switch (target)
        {
            case SquadTarget.Claude:
                format = SquadHookBlockFormat.Claude;
                preToolUse =
                [
                    new JsonObject
                    {
                        ["matcher"] = "^(Agent|Task)$",
                        ["hooks"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = commandLine,
                                ["timeout"] = timeoutSeconds,
                            })
                    },
                    new JsonObject
                    {
                        // A background sub-agent returns through SubagentHandback, not the dispatcher's PostToolUse.
                        ["matcher"] = "^SubagentHandback$",
                        ["hooks"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = commandLine,
                                ["timeout"] = timeoutSeconds,
                            })
                    },
                    CreateMatcherGroup(
                        ClaudeRenderer.WorkerGuardMatcher,
                        ArbiterHookWiring.WorkerGuardCommandLine(SquadTargetCatalog.GetToken(SquadTarget.Claude)),
                        timeoutSeconds)
                ];
                postToolUse =
                [
                    new JsonObject
                    {
                        ["matcher"] = "^(Agent|Task)$",
                        ["hooks"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = commandLine,
                                ["timeout"] = timeoutSeconds,
                            })
                    }
                ];
                return true;

            case SquadTarget.Cursor:
                format = SquadHookBlockFormat.Cursor;
                preToolUse =
                [
                    new JsonObject
                    {
                        ["command"] = commandLine,
                        ["matcher"] = "Task",
                        ["timeout"] = timeoutSeconds,
                        ["failClosed"] = true,
                    }
                ];
                postToolUse =
                [
                    new JsonObject
                    {
                        ["command"] = commandLine,
                        ["matcher"] = "Task",
                        ["timeout"] = timeoutSeconds,
                        ["failClosed"] = true,
                    }
                ];
                return true;

            case SquadTarget.Codex:
                format = SquadHookBlockFormat.Codex;
                preToolUse = [CreateMatcherGroup("^(Agent|(.*[._:/])?spawn_agent)$", commandLine, timeoutSeconds)];
                postToolUse = [CreateMatcherGroup("^(Agent|(.*[._:/])?spawn_agent)$", commandLine, timeoutSeconds)];
                return true;

            case SquadTarget.Factory:
                format = SquadHookBlockFormat.Factory;
                preToolUse = [CreateMatcherGroup("Task", commandLine, timeoutSeconds)];
                postToolUse = [CreateMatcherGroup("Task", commandLine, timeoutSeconds)];
                return true;

            case SquadTarget.Devin:
                format = SquadHookBlockFormat.Devin;
                preToolUse = [CreateMatcherGroup("^run_subagent$", commandLine, timeoutSeconds)];
                postToolUse = [CreateMatcherGroup("^run_subagent$", commandLine, timeoutSeconds)];
                return true;

            case SquadTarget.Antigravity:
                format = SquadHookBlockFormat.Antigravity;
                preToolUse = [CreateMatcherGroup("^invoke_subagent$", commandLine, timeoutSeconds)];
                postToolUse = [CreateMatcherGroup("^invoke_subagent$", commandLine, timeoutSeconds)];
                return true;

            default:
                format = default;
                preToolUse = [];
                postToolUse = [];
                return false;
        }
    }

    /// <summary>One matcher group holding a single command hook; a null matcher omits the key.</summary>
    private static JsonObject CreateMatcherGroup(string? matcher, string commandLine, int timeoutSeconds)
    {
        JsonObject group = new();
        if (matcher is not null)
        {
            group["matcher"] = matcher;
        }

        group["hooks"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "command",
                ["command"] = commandLine,
                ["timeout"] = timeoutSeconds,
            });
        return group;
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
