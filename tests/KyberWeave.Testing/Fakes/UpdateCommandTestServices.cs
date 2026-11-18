using KyberWeave.Application;
using KyberWeave.Cli.Commands.Update;
using KyberWeave.Cli.Composition;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Arbiter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddSingleton<IProcessEnvironment>(FakeProcessEnvironment.UseRunningProcess(_ => null));
        services.AddSingleton<ISelfUpdateSystem>(new HandOffSystem(finishingProcess));
        if (arbiterHookRefresher is not null)
            services.AddSingleton<IArbiterHookRefresherFactory>(new FixedRefresherFactory(arbiterHookRefresher));
        services.TryAddSingleton<IMcpDeclarationReader, McpDeclarationReader>();
        _ = services.AddSquadCliServices();
        _ = services.AddWeaveApplication();
        ServiceProvider provider = services.BuildServiceProvider();
        return new UpdateCommand(provider.GetRequiredService<ISelfUpdateService>());
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

/// <summary>Builds the update's Arbiter seams over the container, for a test's own home and environment.</summary>
internal static class UpdateTestServices
{
    /// <summary>An update that covers no project and no global target.</summary>
    internal static IArbiterRefreshTargets NoProjects { get; } = new NoProjectsArbiterRefreshTargets();

    /// <summary>
    /// The production refresher, resolved from the container over <paramref name="readEnvironment"/>'s home, so a test
    /// exercises the real renderers and receipt rewrites against its own temporary tree.
    /// </summary>
    /// <param name="readEnvironment">Reads one environment variable.</param>
    /// <param name="log">Receives one line per step or warning.</param>
    /// <returns>The refresher.</returns>
    internal static ArbiterHookRefresher Refresher(Func<string, string?> readEnvironment, Action<string>? log = null) =>
        (ArbiterHookRefresher)Container(readEnvironment).GetRequiredService<IArbiterHookRefresherFactory>().Create(log ?? (_ => { }));

    /// <summary>The production refresh targets, resolved from the container over <paramref name="readEnvironment"/>'s home.</summary>
    /// <param name="readEnvironment">Reads one environment variable.</param>
    /// <returns>The targets the update covers.</returns>
    internal static IArbiterRefreshTargets Targets(Func<string, string?> readEnvironment) =>
        Container(readEnvironment).GetRequiredService<IArbiterRefreshTargets>();

    private static ServiceProvider Container(Func<string, string?> readEnvironment)
    {
        ServiceCollection services = [];
        services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(
            () => readEnvironment("HOME") ?? readEnvironment("USERPROFILE") ?? string.Empty,
            readEnvironment));
        services.TryAddSingleton<IMcpDeclarationReader, McpDeclarationReader>();
        _ = services.AddSquadCliServices();
        _ = services.AddWeaveApplication();
        return services.BuildServiceProvider();
    }
}

/// <summary>An update whose registry lists no project and whose user config names no global target.</summary>
internal sealed class NoProjectsArbiterRefreshTargets : IArbiterRefreshTargets
{
    /// <inheritdoc />
    public IReadOnlyList<string> CollectEnabledProjectRoots() => [];

    /// <inheritdoc />
    public IReadOnlyList<string> ReadGlobalTargets(string home) => [];
}
