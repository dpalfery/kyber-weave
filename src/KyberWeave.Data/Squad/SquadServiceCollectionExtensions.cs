using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Host;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.FileSystem;
using KyberWeave.Data.Squad.Release;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Data.Squad;

/// <summary>Registers the Squad adapters.</summary>
public static class SquadServiceCollectionExtensions
{
    /// <summary>Registers the Squad ports and their adapters.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>Every registration uses <c>TryAdd</c>, so a host or test that registers its own implementation first keeps it.</remarks>
    public static IServiceCollection AddWeaveSquadData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISquadReleaseHttpHandlers, SocketSquadReleaseHttpHandlers>();
        services.TryAddSingleton<IFileSystemPathSemantics>(_ => SquadFileSystemPathSemantics.Instance);
        services.TryAddSingleton<ISquadUserPaths>(sp => EnvironmentSquadUserPaths.Create(sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadGlobalRootResolver>(sp => EnvironmentSquadGlobalRoots.Create(
            sp.GetRequiredService<IProcessEnvironment>(),
            sp.GetRequiredService<ISquadFileAccess>()));
        services.TryAddSingleton<ISquadModelPresetStore>(sp => new SquadModelPresetStore(sp.GetRequiredService<ISquadUserPaths>()));
        services.TryAddSingleton<ISquadCanonicalProfileStore>(sp => new SquadCanonicalProfileStore(sp.GetRequiredService<ISquadUserPaths>()));
        services.TryAddSingleton<ISquadSourceLoader>(sp => new SquadSourceLoader(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadCompanionSkills>(sp => new SquadCompanionSkills(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ICopilotMcpServerProbe>(sp => new CopilotMcpServerProbe(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ISquadOperatorChannelOverlayFile>(sp => new SquadOperatorChannelOverlayFile(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ISquadWorkerBaselineFile>(sp => new SquadWorkerBaselineFile(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadRenderInputReader>(sp => new FileSquadRenderInputReader(
            sp.GetRequiredService<ISquadSourceLoader>(),
            sp.GetRequiredService<ICopilotMcpServerProbe>(),
            sp.GetRequiredService<ISquadOperatorChannelOverlayFile>(),
            sp.GetRequiredService<ISquadWorkerBaselineFile>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadModelPresetValidator>(sp => new SquadModelPresetValidator(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ISquadDeploymentPlanner>(sp => new SquadDeploymentPlanner(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IFileSystemPathSemantics>(),
            sp.GetRequiredService<IPathResolver>()));
        services.TryAddSingleton<ISquadTransactionFactory>(sp => new SquadTransactionFactory(
            sp.GetRequiredService<ISquadStateStore>(),
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IFileSystemPathSemantics>(),
            sp.GetRequiredService<ILease>(),
            sp.GetRequiredService<IIdGenerator>()));
        services.TryAddSingleton<ISquadResponseGuidanceStore>(sp => new SquadResponseGuidanceReader(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ISquadFactoryHooksShadowing>(sp => new FactoryHooksShadowing(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ISquadInstanceLinkSync>(sp => new SquadInstanceLinkSync(
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<ILease>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadFileAccess, PhysicalSquadFileAccess>();
        services.TryAddSingleton<IConsoleProcessLauncher, ProcessConsoleLauncher>();
        services.TryAddSingleton<IUpdateFileModes, UnixUpdateFileModes>();
        services.TryAddSingleton<ISelfUpdateSystem>(sp => new SelfUpdateSystem(
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IBoundedProcessExecutor>(),
            sp.GetRequiredService<IConsoleProcessLauncher>(),
            sp.GetRequiredService<IUpdateFileModes>()));
        services.TryAddSingleton<ISquadPackager>(sp => new FileSquadPackager(
            sp.GetRequiredService<ISquadArchiveWriter>(),
            sp.GetRequiredService<ISquadSourceLoader>(),
            sp.GetRequiredService<ISquadCompanionSkills>()));
        services.TryAddSingleton<ISquadSkillShadowing>(sp => new SquadSkillShadowing(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IDevinImportOverlapInspector>(sp => new DevinImportOverlap(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IZCodeMcpInspector>(sp => new ZCodeMcpConfiguration(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadWorktreeMcp>(sp => new SquadWorktreeMcp(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadHookJsonFile>(sp => new SquadHookJsonFile(
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadHarnessStartInfoBuilder>(sp => new SquadHarnessLauncher(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadHarnessLauncher>(sp => new ProcessSquadHarnessLauncher(
            sp.GetRequiredService<ISquadHarnessStartInfoBuilder>()));
        services.TryAddSingleton<ISquadSourceLocator>(sp => new SquadPackSourceLocator(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISquadTargetResolver>(sp => new SquadTargetResolver(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ILease, MutexLease>();
        services.TryAddSingleton<ISquadCanonicalProfileResolver, CheckoutOrSnapshotCanonicalProfileResolver>();
        services.TryAddSingleton<ISquadLinkFileSystem>(_ => OperatingSystem.IsWindows()
            ? new WindowsSquadLinkFileSystem()
            : new UnixSquadLinkFileSystem());
        return services;
    }
}

/// <summary>Resolves the user paths Squad state is anchored to from the process environment.</summary>
internal static class EnvironmentSquadUserPaths
{
    /// <summary>Resolves the paths from HOME, USERPROFILE or APPDATA, falling back to the system folders.</summary>
    /// <param name="environment">The process environment.</param>
    /// <returns>The user paths.</returns>
    public static ISquadUserPaths Create(IProcessEnvironment environment)
    {
        string? home = environment.GetVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = environment.GetVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home)) home = environment.HomeDirectory;
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string? appData = environment.GetVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData) && !string.IsNullOrWhiteSpace(home))
            appData = Path.Combine(home, ".config");

        return new SquadUserPaths(home ?? string.Empty, appData ?? string.Empty);
    }
}

/// <summary>Resolves each target's global root from the process environment.</summary>
internal static class EnvironmentSquadGlobalRoots
{
    /// <summary>Builds the resolver over the environment and the file probes.</summary>
    /// <param name="environment">The process environment.</param>
    /// <param name="files">Reads the one config file a global root can be set in.</param>
    /// <returns>The resolver.</returns>
    public static ISquadGlobalRootResolver Create(IProcessEnvironment environment, ISquadFileAccess files)
    {
        string? home = environment.GetVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = environment.GetVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home)) home = environment.HomeDirectory;
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new SquadGlobalRoots(
            environment.GetVariable,
            home ?? string.Empty,
            path =>
            {
                try
                {
                    return files.FileExists(path) ? files.ReadAllText(path) : null;
                }
                catch (Exception ex) when (FileFailure.Is(ex))
                {
                    return null;
                }
            });
    }
}

/// <summary>The home and application-data directories Squad state is anchored to.</summary>
/// <param name="HomeDirectory">The user's home directory.</param>
/// <param name="ApplicationDataDirectory">The user's application-data directory.</param>
internal sealed record SquadUserPaths(string HomeDirectory, string ApplicationDataDirectory) : ISquadUserPaths;
