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
        if (projectRoots.Count == 0)
            return default;

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

        return new ArbiterHookRefreshResult(refreshed, failed);
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
