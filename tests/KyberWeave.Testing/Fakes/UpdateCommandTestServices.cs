using KyberWeave.Application;
using KyberWeave.Cli.Commands.Update;
using KyberWeave.Cli.Composition;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Processes;
using KyberWeave.Testing.Update;
using Spectre.Console.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console;
using KyberWeave.Domain.Update;

namespace KyberWeave.Tests.Fakes;

/// <summary>An <see cref="UpdateCommand"/> and the console it writes to.</summary>
/// <param name="Command">The command.</param>
/// <param name="Console">What the command wrote, as an operator would have seen it.</param>
internal sealed record UpdateCommandRun(UpdateCommand Command, TestConsole Console)
{
    /// <summary>Gets everything the command wrote to the console so far.</summary>
    internal string Output => Console.Output;
}

/// <summary>Builds an <see cref="UpdateCommand"/> over the application container, with the host, release traffic, hand-off and console replaced by fakes.</summary>
internal static class UpdateCommandTestServices
{
    /// <summary>Creates the command over a world's in-memory install.</summary>
    /// <param name="world">The in-memory install, file system and update system.</param>
    /// <param name="host">The running binary's snapshot.</param>
    /// <param name="handler">Serves the release traffic.</param>
    /// <param name="finishingProcess">Starts the hand-off child; the update system's fake launcher when null.</param>
    /// <returns>The command and its console.</returns>
    internal static UpdateCommandRun Create(
        UpdateWorld world,
        SelfUpdateHost host,
        HttpMessageHandler handler,
        Func<string, IReadOnlyList<string>, bool, int>? finishingProcess = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        TestConsole console = world.CreateConsole(240);
        ServiceProvider provider = world.BuildServices(
            world.HomeEnvironment(),
            configure: services =>
            {
                _ = services.AddSingleton<Func<SelfUpdateHost>>(() => host);
                _ = services.AddSingleton(handler);
                _ = services.AddSingleton<ISelfUpdateSystem>(new HandOffSystem(world.CreateSystem(), finishingProcess));
                _ = services.AddSingleton<IAnsiConsole>(console);
            });
        return new UpdateCommandRun(
            new UpdateCommand(
                provider.GetRequiredService<ISelfUpdateService>(),
                provider.GetRequiredService<IAnsiConsole>()),
            console);
    }

    private sealed class HandOffSystem(ISelfUpdateSystem inner, Func<string, IReadOnlyList<string>, bool, int>? finishingProcess) : ISelfUpdateSystem
    {
        public int RunProcess(string fileName, IReadOnlyList<string> arguments) => inner.RunProcess(fileName, arguments);

        public Task<ProcessResult> RunCaptureAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            inner.RunCaptureAsync(fileName, arguments, cancellationToken);

        public int StartFinishingProcess(string fileName, IReadOnlyList<string> arguments, bool discardOutput) =>
            finishingProcess is null
                ? inner.StartFinishingProcess(fileName, arguments, discardOutput)
                : finishingProcess(fileName, arguments, discardOutput);

        public bool CanWriteDirectory(string directory) => inner.CanWriteDirectory(directory);

        public void ExtractArchive(string archivePath, string destinationDirectory, bool windows) =>
            inner.ExtractArchive(archivePath, destinationDirectory, windows);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite) =>
            inner.CopyFile(sourcePath, destinationPath, overwrite);

        public Task ReplaceBinaryAsync(
            string extractedBinaryPath,
            string destinationPath,
            bool windows,
            bool macOs,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceBinaryAsync(extractedBinaryPath, destinationPath, windows, macOs, cancellationToken);
    }
}

/// <summary>The update's Arbiter refresh targets for a test.</summary>
internal static class UpdateTestServices
{
    /// <summary>An update that covers no project and no global target.</summary>
    internal static IArbiterRefreshTargets NoProjects { get; } = new NoProjectsArbiterRefreshTargets();
}

/// <summary>An update whose registry lists no project and whose user config names no global target.</summary>
internal sealed class NoProjectsArbiterRefreshTargets : IArbiterRefreshTargets
{
    /// <inheritdoc />
    public IReadOnlyList<string> CollectEnabledProjectRoots() => [];

    /// <inheritdoc />
    public IReadOnlyList<string> ReadGlobalTargets(string home) => [];
}
