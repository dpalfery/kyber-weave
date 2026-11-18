using KyberWeave.Application.Squad.Packaging;
using KyberWeave.Application.Update;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Application.Squad;

/// <summary>Registers the Squad use cases.</summary>
public static class SquadServiceCollectionExtensions
{
    /// <summary>Registers the Squad lifecycle and instance services over the Squad ports.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// The host registers <see cref="ISquadRenderer"/>: which renderers exist is the host's choice. Where a release
    /// comes from defaults to the GitHub release origin, and to a local checkout for the instance service.
    /// The host registers a <c>Func&lt;SelfUpdateHost&gt;</c>, <see cref="IArbiterHookRefresherFactory"/> and <see cref="IArbiterRefreshTargets"/>
    /// for <see cref="ISelfUpdateService"/>. Every registration uses <c>TryAdd</c>, so a host or test that registers its own implementation first keeps it.
    /// </remarks>
    public static IServiceCollection AddWeaveSquadApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISquadReleaseSource>(sp => new GitHubSquadReleaseSource(
            ReleaseOrigin.Resolve(sp.GetRequiredService<IProcessEnvironment>().GetVariable).ApiRoot));
        services.TryAddSingleton<ISquadInstanceReleaseSource>(sp => new InstanceReleaseSource(
            FindLocalProductSquadRoot(sp.GetRequiredService<ISquadFileAccess>()) is { } local
                ? new LocalCheckoutSquadReleaseSource(local)
                : sp.GetRequiredService<ISquadReleaseSource>()));
        services.TryAddSingleton<ISquadModelPresetSelectionStore>(sp => new SquadModelPresetSelectionStore(
            sp.GetRequiredService<ISquadUserPaths>(),
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IFileSystemPathSemantics>()));
        services.TryAddSingleton<ISquadPackService, SquadPackService>();
        services.TryAddSingleton<ISquadArchiveWriter>(sp => new SquadPacker(
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<ISquadCompanionSkills>()));
        services.TryAddSingleton<IArbiterRefreshTargets, CliArbiterRefreshTargets>();
        services.TryAddSingleton<IArbiterHookRefresherFactory, ArbiterHookRefresherFactory>();
        services.TryAddSingleton<ISelfUpdateService>(sp => new SelfUpdateService(
            sp.GetRequiredService<Func<SelfUpdateHost>>(),
            sp.GetRequiredService<IProcessEnvironment>(),
            sp.GetRequiredService<ISelfUpdateSystem>(),
            sp.GetRequiredService<IArbiterHookRefresherFactory>(),
            sp.GetRequiredService<IArbiterRefreshTargets>(),
            sp.GetService<HttpMessageHandler>()));
        services.TryAddSingleton<ISquadLifecycleService>(sp => CreateLifecycle(sp, sp.GetRequiredService<ISquadReleaseSource>()));
        services.TryAddSingleton<ISquadInstanceService>(sp => new SquadInstanceService(
            CreateLifecycle(sp, sp.GetService<ISquadInstanceReleaseSource>() ?? sp.GetRequiredService<ISquadReleaseSource>()),
            sp.GetRequiredService<ISquadModelPresetStore>(),
            sp.GetRequiredService<ISquadLinkFileSystem>(),
            sp.GetRequiredService<ISquadGlobalRootResolver>(),
            sp.GetRequiredService<ISquadInstanceLinkSync>(),
            sp.GetRequiredService<ISquadFileAccess>(),
            sp.GetRequiredService<ISquadStateStore>()));
        return services;
    }

    private static string? FindLocalProductSquadRoot(ISquadFileAccess files)
    {
        try
        {
            return files.FindCheckoutSquadSourceRoot();
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }

    /// <summary>The instance service's release source: a local checkout when one is found, otherwise the download.</summary>
    private sealed class InstanceReleaseSource(ISquadReleaseSource inner) : ISquadInstanceReleaseSource
    {
        public Task<SquadReleaseResult> DownloadAndExtractAsync(SquadReleaseRequest request, CancellationToken cancellationToken) =>
            inner.DownloadAndExtractAsync(request, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    internal static SquadLifecycleService CreateLifecycle(IServiceProvider sp, ISquadReleaseSource releaseSource) =>
        new(
            releaseSource,
            sp.GetRequiredService<ISquadRenderer>(),
            sp.GetRequiredService<ISquadStateStore>(),
            sp.GetRequiredService<ISquadModelPresetStore>(),
            sp.GetRequiredService<ISquadModelPresetSelectionStore>(),
            sp.GetRequiredService<ISquadSourceLoader>(),
            sp.GetRequiredService<ISquadModelPresetValidator>(),
            sp.GetRequiredService<ISquadDeploymentPlanner>(),
            sp.GetRequiredService<ISquadTransactionFactory>(),
            sp.GetRequiredService<ISquadResponseGuidanceStore>(),
            sp.GetRequiredService<ISquadFactoryHooksShadowing>(),
            sp.GetRequiredService<ISquadCanonicalProfileStore>(),
            sp.GetRequiredService<ISquadFileAccess>(),
            sp.GetRequiredService<IFileSystemPathSemantics>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ISquadTransactionObserver>(),
            sp.GetRequiredService<ISquadGlobalRootResolver>(),
            sp.GetRequiredService<IProcessEnvironment>().GetVariable);
}
