using KyberWeave.Application;
using KyberWeave.Cli.Commands.Update;
using KyberWeave.Cli.Composition;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests.Fakes;

/// <summary>Builds an <see cref="UpdateCommand"/> over the application container, with the host, release traffic and hand-off replaced by fakes.</summary>
internal static class UpdateCommandTestServices
{
    /// <summary>Creates the command.</summary>
    /// <param name="host">The running binary's snapshot.</param>
    /// <param name="handler">Serves the release traffic.</param>
    /// <param name="finishingProcess">Starts the hand-off child; null starts a real one.</param>
    /// <param name="arbiterHookRefresher">Replaces the arbiter hook refresher; null keeps the production one over an empty environment.</param>
    /// <returns>The command.</returns>
    internal static UpdateCommand Create(
        SelfUpdateHost host,
        HttpMessageHandler handler,
        Func<string, IReadOnlyList<string>, bool, int>? finishingProcess = null,
        IArbiterHookRefresher? arbiterHookRefresher = null)
    {
        ServiceCollection services = [];
        services.AddSingleton<Func<SelfUpdateHost>>(() => host);
        services.AddSingleton(handler);
        services.AddSingleton<IProcessEnvironment>(new EmptyEnvironment());
        services.AddSingleton<ISelfUpdateSystem>(new HandOffSystem(finishingProcess));
        Func<string, string?> empty = _ => null;
        services.AddSingleton<IArbiterRefreshTargets>(new CliArbiterRefreshTargets(empty));
        services.AddSingleton<IArbiterHookRefresherFactory>(arbiterHookRefresher is null
            ? new ArbiterHookRefresherFactory(empty)
            : new FixedRefresherFactory(arbiterHookRefresher));
        _ = services.AddWeaveApplication();
        ServiceProvider provider = services.BuildServiceProvider();
        return new UpdateCommand(provider.GetRequiredService<ISelfUpdateService>());
    }

    private sealed class EmptyEnvironment : IProcessEnvironment
    {
        public string CurrentDirectory => Environment.CurrentDirectory;

        public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public string TempDirectory => Path.GetTempPath();

        public string? GetVariable(string name) => null;
    }

    private sealed class FixedRefresherFactory(IArbiterHookRefresher refresher) : IArbiterHookRefresherFactory
    {
        public IArbiterHookRefresher Create(Action<string> log) => refresher;
    }

    private sealed class HandOffSystem(Func<string, IReadOnlyList<string>, bool, int>? finishingProcess) : ISelfUpdateSystem
    {
        private readonly ISelfUpdateSystem _inner = new KyberWeave.Cli.Update.SelfUpdateSystem();

        public int RunProcess(string fileName, IReadOnlyList<string> arguments) => _inner.RunProcess(fileName, arguments);

        public ProcessResult RunCapture(string fileName, IReadOnlyList<string> arguments) => _inner.RunCapture(fileName, arguments);

        public int StartFinishingProcess(string fileName, IReadOnlyList<string> arguments, bool discardOutput) =>
            finishingProcess is null
                ? _inner.StartFinishingProcess(fileName, arguments, discardOutput)
                : finishingProcess(fileName, arguments, discardOutput);

        public bool CanWriteDirectory(string directory) => _inner.CanWriteDirectory(directory);

        public void ExtractArchive(string archivePath, string destinationDirectory, bool windows) =>
            _inner.ExtractArchive(archivePath, destinationDirectory, windows);

        public void ReplaceBinary(string extractedBinaryPath, string destinationPath, bool windows, bool macOs) =>
            _inner.ReplaceBinary(extractedBinaryPath, destinationPath, windows, macOs);

        public bool RefreshCopilotPrompt(string homeDirectory, Action<string> log) => _inner.RefreshCopilotPrompt(homeDirectory, log);
    }
}
