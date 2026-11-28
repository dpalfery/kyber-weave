using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Arbiter;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Application.Update;

/// <summary>
/// Default arbiter-hook refresh: re-renders hooks for each enabled project through the Squad
/// update path, in-process. A project whose deployment is missing or unreadable is skipped with a
/// warning; the update that already committed its binaries keeps its exit code.
/// </summary>
internal sealed class ArbiterHookRefresher(
    IProcessEnvironment environment,
    IUserHostConfigStore userConfig,
    IKyberWeaveConfigRepository configs,
    ISquadLifecycleService lifecycle,
    ISquadStateStore state,
    IWritableFileSystem files,
    IMcpWiringService wiring,
    Action<string>? log = null) : IArbiterHookRefresher
{
    private readonly Func<string, string?> _environmentReader = environment.GetVariable;

    /// <inheritdoc />
    public async Task<ArbiterHookRefreshResult> RefreshAsync(
        IReadOnlyList<string> projectRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);

        int refreshed = 0;
        int failed = 0;
        foreach (string root in projectRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            KyberWeaveConfigLoadResult loaded;
            try
            {
                loaded = configs.TryLoad(root);
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                log?.Invoke($"warning: skipping arbiter hook refresh for '{root}': {ex.Message}");
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
                SquadArbiterWiring wiring = Wiring(loaded.Config.Arbiter);
                SquadUpdateRequest request = new(root, SquadDeploymentScope.Project, Arbiter: wiring);
                SquadLifecycleResult result = await lifecycle
                    .UpdateAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Success)
                {
                    string reason = result.Errors is { Count: > 0 }
                        ? string.Join(Environment.NewLine, result.Errors)
                        : "the Squad update reported failure.";
                    log?.Invoke($"warning: arbiter hook refresh for '{root}' failed: {reason}");
                    failed++;
                }
                else
                {
                    refreshed++;
                }
            }
            catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException or HttpRequestException or TimeoutException or OperationCanceledException or InvalidDataException)
            {
                log?.Invoke($"warning: arbiter hook refresh for '{root}' failed: {ex.Message}");
                failed++;
            }
        }

        (int globalRefreshed, int globalFailed) = RefreshGlobalHooks();
        return new ArbiterHookRefreshResult(refreshed + globalRefreshed, failed + globalFailed);
    }

    private (int Refreshed, int Failed) RefreshGlobalHooks()
    {
        string? home = ResolveHome();
        int globalRefreshed = 0;
        int globalFailed = 0;
        if (string.IsNullOrWhiteSpace(home))
        {
            return (globalRefreshed, globalFailed);
        }

        int globalTimeout = ResolveGlobalTimeout(home);
        SquadReceipt? receipt = null;
        try
        {
            receipt = state.ReadReceipt(home, SquadDeploymentScope.Global);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
        {
            log?.Invoke($"warning: could not read global squad receipt: {ex.Message}");
        }

        bool receiptChanged = false;
        List<SquadOwnedFile>? updatedFiles = receipt is not null ? new(receipt.Files) : null;
        List<SquadOwnedBlock>? updatedBlocks = receipt is not null ? new(receipt.Blocks) : null;

        foreach ((string? targetToken, string? hookPath, string? hookRelativePath) in ResolveGlobalHookTargets(home))
        {
            if (string.IsNullOrWhiteSpace(hookPath))
                continue;

            if (!files.FileExists(hookPath))
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
                            int blockIdx = FindOwnedIndex(
                                updatedBlocks, targetToken, hookRelativePath, b => b.Target, b => b.RelativePath);
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
                            int fileIdx = FindOwnedIndex(
                                updatedFiles, targetToken, hookRelativePath, f => f.Target, f => f.RelativePath);
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
            catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException or JsonException)
            {
                log?.Invoke($"warning: arbiter global hook refresh for '{targetToken}' failed: {ex.Message}");
                globalFailed++;
            }
        }

        PersistGlobalReceiptIfChanged(home, receipt, receiptChanged, updatedFiles, updatedBlocks);

        return (globalRefreshed, globalFailed);
    }

    private void PersistGlobalReceiptIfChanged(
        string home,
        SquadReceipt? receipt,
        bool receiptChanged,
        List<SquadOwnedFile>? updatedFiles,
        List<SquadOwnedBlock>? updatedBlocks)
    {
        if (receipt is null || !receiptChanged)
        {
            return;
        }

        try
        {
            SquadReceipt updatedReceipt = receipt with
            {
                Files = updatedFiles ?? receipt.Files,
                Blocks = updatedBlocks ?? receipt.Blocks
            };
            string receiptPath = state.ResolveReceiptPath(home, SquadDeploymentScope.Global);
            string serialized = state.SerializeReceipt(updatedReceipt);
            files.WriteAllTextAtomic(receiptPath, serialized);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException)
        {
            log?.Invoke($"warning: updating global squad receipt failed: {ex.Message}");
        }
    }

    private string? ResolveHome()
    {
        string? home = _environmentReader("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = _environmentReader("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home))
            home = environment.HomeDirectory;

        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    private static SquadArbiterWiring Wiring(ArbiterConfig arbiter) =>
        new(arbiter.Enabled, ArbiterHookTimeout.SecondsFor(arbiter.Provider.TimeoutMs));

    private int ResolveGlobalTimeout(string home)
    {
        try
        {
            KyberWeaveConfigLoadResult userConfig = configs.TryLoadUserConfig(home);
            if (userConfig.Success && userConfig.Config?.Arbiter is { } arbiter)
            {
                return Wiring(arbiter).HookTimeoutSeconds;
            }
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            // Fall back to default.
        }

        return Wiring(ArbiterConfigDefaults.Product).HookTimeoutSeconds;
    }

    private IEnumerable<(string TargetToken, string HookPath, string? RelativePath)> ResolveGlobalHookTargets(string home)
    {
        SquadGlobalRoots roots = new(_environmentReader, home);
        foreach (string token in userConfig.ReadGlobalTargets(home))
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
                continue;

            string? hookPath = ArbiterGlobalHookPaths.Resolve(target, home, roots);
            if (!string.IsNullOrWhiteSpace(hookPath))
            {
                yield return (
                    SquadTargetCatalog.GetToken(target),
                    hookPath,
                    RelativePathUnderGlobalRoot(roots, target, hookPath));
            }
        }
    }

    /// <summary>
    /// The hook file's receipt path, relative to its target's own global root, so a receipt entry
    /// is picked out by path as well as target. Null when the path cannot be expressed as a
    /// portable relative path, which leaves the caller with the unambiguous single-target match.
    /// </summary>
    private static string? RelativePathUnderGlobalRoot(ISquadGlobalRootResolver roots, SquadTarget target, string hookPath)
    {
        try
        {
            string relative = Path.GetRelativePath(roots.ResolveGlobalRoot(target), hookPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            return SquadPathPolicy.NormalizeRelativePath(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the receipt entry an updated hook file belongs to: the same target, and the same path
    /// when the caller can derive it. A target is not an identity — a global receipt records many
    /// files for one harness — so a target with several entries and no path match answers -1
    /// rather than the first entry, which is what stamped a hook file's digest onto an agent file.
    /// </summary>
    private static int FindOwnedIndex<T>(
        List<T> entries,
        string targetToken,
        string? relativePath,
        Func<T, string> targetOf,
        Func<T, string> pathOf)
    {
        int singleTargetMatch = -1;
        int targetMatches = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            if (!string.Equals(targetOf(entries[index]), targetToken, StringComparison.OrdinalIgnoreCase))
                continue;

            targetMatches++;
            singleTargetMatch = index;
            if (relativePath is not null
                && string.Equals(pathOf(entries[index]), relativePath, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return targetMatches == 1 ? singleTargetMatch : -1;
    }

    private bool RefreshGlobalHook(
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

        if (target is SquadTarget.Kilo or SquadTarget.OpenCode or SquadTarget.Pi or SquadTarget.Copilot
            or SquadTarget.CommandCode)
        {
            string rendered = target switch
            {
                SquadTarget.Kilo => ArbiterPluginShim.Render(HarnessRegistry.KiloToken, SquadDeploymentScope.Global),
                SquadTarget.OpenCode => ArbiterPluginShim.Render(HarnessRegistry.OpenCodeToken, SquadDeploymentScope.Global),
                SquadTarget.Pi => PiRenderer.RenderArbiterExtension(timeoutSeconds, SquadDeploymentScope.Global),
                SquadTarget.Copilot => Encoding.UTF8.GetString(
                    CopilotRenderer.RenderCliHooksFile(timeoutSeconds, SquadDeploymentScope.Global, "hooks/kyber-arbiter.json").Content.Span),
                SquadTarget.CommandCode => CommandCodeRenderer.RenderArbiterMod(timeoutSeconds, SquadDeploymentScope.Global),
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(rendered))
                return false;

            string before = files.FileExists(hookPath) ? files.ReadAllText(hookPath) : string.Empty;
            if (string.Equals(before, rendered, StringComparison.Ordinal))
                return false;

            files.WriteAllTextAtomic(hookPath, rendered);
            newContent = rendered;
            return true;
        }

        if (!TryBuildGlobalHookEntries(
                target, timeoutSeconds, out SquadHookBlockFormat format,
                out JsonNode[] preToolUse, out JsonNode[] postToolUse, out JsonNode[] subagentStop))
            return false;

        string existing = files.FileExists(hookPath) ? files.ReadAllText(hookPath) : string.Empty;
        if (string.IsNullOrWhiteSpace(existing))
            return false;

        // The Factory worker guard joins the session events, since a tool-call payload names no
        // subagent. Refreshing without them would stop the guard from ever learning a child.
        string factoryWorkerGuard = ArbiterHookWiring.WorkerGuardCommandLine(SquadTargetCatalog.GetToken(SquadTarget.Factory));
        IReadOnlyList<JsonNode> sessionStart = format == SquadHookBlockFormat.Factory
            ? [CreateMatcherGroup(null, factoryWorkerGuard, timeoutSeconds)]
            : [];
        IReadOnlyList<JsonNode> sessionEnd = format == SquadHookBlockFormat.Factory
            ? [CreateMatcherGroup(null, factoryWorkerGuard, timeoutSeconds)]
            : [];

        (string spliced, IReadOnlyList<SquadHookOwnedEntry> owned) = SquadHookJsonBlock.SpliceContent(
            format, existing, preToolUse, postToolUse, subagentStop, sessionStart, sessionEnd);
        if (string.Equals(existing, spliced, StringComparison.Ordinal))
            return false;

        files.WriteAllTextAtomic(hookPath, spliced);
        newContent = spliced;
        newOwnedEntries = owned;
        return true;
    }

    private static bool TryBuildGlobalHookEntries(
        SquadTarget target,
        int timeoutSeconds,
        out SquadHookBlockFormat format,
        out JsonNode[] preToolUse,
        out JsonNode[] postToolUse,
        out JsonNode[] subagentStop)
    {
        string commandLine = ArbiterHookWiring.HookCommandLine(target, SquadDeploymentScope.Global);
        subagentStop = [];
        switch (target)
        {
            case SquadTarget.Claude:
                format = SquadHookBlockFormat.Claude;
                // Claude Code fires SubagentStop with no matcher. Splicing without it would strip the
                // registration the install wrote, so the refresh writes it again.
                subagentStop = [CreateMatcherGroup(null, commandLine, timeoutSeconds)];
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
                    },
                    // A background agent stopped with TaskStop never ends its turn; this entry closes its dispatch.
                    CreateMatcherGroup(ClaudeRenderer.StopMatcher, commandLine, timeoutSeconds)
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
                // Factory's global file also carries the SubagentStop group: its launch-time PostToolUse
                // carries no worker report, so the Arbiter reads the report on this container.
                subagentStop = [CreateMatcherGroup(null, commandLine, timeoutSeconds)];
                string factoryWorkerGuard = ArbiterHookWiring.WorkerGuardCommandLine(
                    SquadTargetCatalog.GetToken(SquadTarget.Factory));
                preToolUse =
                [
                    CreateMatcherGroup("Task", commandLine, timeoutSeconds),
                    CreateMatcherGroup("*", factoryWorkerGuard, timeoutSeconds)
                ];
                postToolUse =
                [
                    CreateMatcherGroup("Task", commandLine, timeoutSeconds),
                    CreateMatcherGroup("Task", factoryWorkerGuard, timeoutSeconds)
                ];
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
            .. wiring.Scan(root, null)
                .Where(change => string.Equals(change.Code, McpServerRenamer.RenameCode, StringComparison.Ordinal))
        ];

        foreach (McpRenameChange leftover in leftovers)
        {
            log?.Invoke(
                $"warning: '{root}' still holds the retired MCP server key '{leftover.OldKey}' "
                + $"({leftover.Path}): {McpServerRenamer.RenameCode}; run {McpServerRenamer.MigrateCommand} "
                + $"--target {leftover.Harness} to rename it to '{leftover.NewKey}'.");
        }
    }
}
