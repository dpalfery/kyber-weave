using KyberWeave.Contracts.Host;
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
        services.TryAddSingleton<ISquadSourceLoader, FileSquadSourceLoader>();
        services.TryAddSingleton<ISquadCompanionSkills, FileSquadCompanionSkills>();
        services.TryAddSingleton<ISquadRenderInputReader, FileSquadRenderInputReader>();
        services.TryAddSingleton<ISquadModelPresetValidator, FileSquadModelPresetValidator>();
        services.TryAddSingleton<ISquadDeploymentPlanner, FileSquadDeploymentPlanner>();
        services.TryAddSingleton<ISquadTransactionFactory>(sp => new SquadTransactionFactory(sp.GetRequiredService<ISquadStateStore>()));
        services.TryAddSingleton<ISquadResponseGuidanceStore, FileSquadResponseGuidanceStore>();
        services.TryAddSingleton<ISquadFactoryHooksShadowing, FileFactoryHooksShadowing>();
        services.TryAddSingleton<ISquadInstanceLinkSync, FileSquadInstanceLinkSync>();
        services.TryAddSingleton<ISquadFileAccess, PhysicalSquadFileAccess>();
        services.TryAddSingleton<ISelfUpdateSystem, SelfUpdateSystem>();
        services.TryAddSingleton<ISquadPackager>(sp => new FileSquadPackager(sp.GetRequiredService<ISquadArchiveWriter>()));
        services.TryAddSingleton<ISquadSkillShadowing, FileSquadSkillShadowing>();
        services.TryAddSingleton<IDevinImportOverlapInspector, FileDevinImportOverlapInspector>();
        services.TryAddSingleton<IZCodeMcpInspector, FileZCodeMcpInspector>();
        services.TryAddSingleton<ISquadHookJsonFile, FileSquadHookJsonFile>();
        services.TryAddSingleton<ISquadHarnessLauncher, ProcessSquadHarnessLauncher>();
        services.TryAddSingleton<ISquadSourceLocator, FileSquadSourceLocator>();
        services.TryAddSingleton<ISquadTargetResolver, FileSquadTargetResolver>();
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
