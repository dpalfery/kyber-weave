using KyberWeave.Application.Doctor;
using KyberWeave.Application.Hooks;
using KyberWeave.Application.RunStart;
using KyberWeave.Application.Utilities;
using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Contracts.Services;
using KyberWeave.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Application;

/// <summary>Composition root for the application layer.</summary>
/// <remarks>
/// This is the one place above Data allowed to name it: it delegates adapter registration there, then registers the
/// services on top. Every registration uses <c>TryAdd</c>, so a host or test that registers its own implementation
/// first keeps it.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the data layer and every application service.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddWeaveApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddWeaveDataServices();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEditHookService, EditHookService>();
        services.TryAddSingleton<IRunStartRunnerFactory, RunStartRunnerFactory>();
        services.TryAddSingleton<ISquadBriefService, SquadBriefService>();
        services.TryAddSingleton<IStatusLineDeploymentService, StatusLineDeploymentService>();
        services.TryAddSingleton<ICodeGraphProvisioner, CodeGraphIndexProvisioner>();
        services.TryAddSingleton<IWorktreeIndexGuard, WorktreeIndexEnsurer>();
        services.TryAddSingleton<McpServerLiveProbe>();
        services.TryAddSingleton<PostCheckoutHookInstaller>();
        services.TryAddSingleton<IRepositoryDoctor, RepositoryDoctorService>();
        return services;
    }
}
