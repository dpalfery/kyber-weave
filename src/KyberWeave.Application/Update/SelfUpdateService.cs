using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Application.Update;

/// <summary>Runs one self-update over the host's snapshot of the running binary.</summary>
/// <remarks>
/// A test registers an <see cref="HttpMessageHandler"/> to serve the release traffic; without one the update uses a
/// handler that follows redirects, as the installed binary does.
/// </remarks>
internal sealed class SelfUpdateService(
    Func<SelfUpdateHost> host,
    IProcessEnvironment environment,
    ISelfUpdateSystem system,
    IArbiterHookRefresherFactory refreshers,
    IArbiterRefreshTargets targets,
    CopilotPromptRefresher copilotPrompt,
    HttpMessageHandler? handler = null) : ISelfUpdateService
{
    /// <inheritdoc />
    public SelfUpdateOutcome Run(SelfUpdateOptions options, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        SelfUpdateHost snapshot = host();
        IArbiterHookRefresher refresher = refreshers.Create(log);
        using SelfUpdater updater = handler is null
            ? SelfUpdater.Create(snapshot, log, refresher, system: system, arbiterTargets: targets, readEnvironment: environment.GetVariable, copilotPrompt: copilotPrompt)
            : new SelfUpdater(
                handler,
                snapshot,
                log,
                environment.GetVariable,
                arbiterHookRefresher: refresher,
                system: system,
                arbiterTargets: targets,
                copilotPrompt: copilotPrompt);
        return updater.Run(options);
    }
}
