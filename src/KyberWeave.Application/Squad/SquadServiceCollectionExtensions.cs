using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
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
    /// The host registers <see cref="ISquadReleaseSource"/>, <see cref="ISquadInstanceReleaseSource"/> and
    /// <see cref="ISquadRenderer"/>: where a release comes from and which renderers exist are the host's choice.
    /// Every registration uses <c>TryAdd</c>, so a host or test that registers its own implementation first keeps it.
    /// </remarks>
    public static IServiceCollection AddWeaveSquadApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

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

    private static SquadLifecycleService CreateLifecycle(IServiceProvider sp, ISquadReleaseSource releaseSource) =>
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
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ISquadTransactionObserver>(),
            sp.GetRequiredService<ISquadGlobalRootResolver>(),
            sp.GetRequiredService<IProcessEnvironment>().GetVariable);
}
