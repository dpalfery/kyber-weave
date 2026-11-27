using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Update;

namespace KyberWeave.Application.Update;

/// <summary>Runs one self-update over the host's snapshot of the running binary.</summary>
/// <remarks>
/// A test registers an <see cref="HttpMessageHandler"/> to serve the release traffic; without one the update uses a
/// handler that follows redirects, as the installed binary does.
/// </remarks>
internal sealed class SelfUpdateService(
    Func<SelfUpdateHost> host,
    IProcessEnvironment environment,
    IWritableFileSystem files,
    IPathResolver paths,
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
        HttpMessageHandler? owned = handler is null ? new SocketsHttpHandler { AllowAutoRedirect = true } : null;
        using SelfUpdater updater = new(
            handler ?? owned!,
            snapshot,
            files,
            paths,
            environment,
            system,
            log,
            arbiterHookRefresher: refresher,
            arbiterTargets: targets,
            copilotPrompt: copilotPrompt);
        try
        {
            return updater.Run(options);
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
