using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Configuration;
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
    public void Refresh(IReadOnlyList<string> projectRoots)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);
        if (projectRoots.Count == 0)
            return;

        SquadLifecycleService lifecycle =
            SquadCommandComposition.CreateLifecycleService(environmentReader: _environmentReader);
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
                continue;
            }

            if (!loaded.Success || loaded.Config?.Arbiter.Enabled != true)
                continue;

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
            }
        }
    }
}
