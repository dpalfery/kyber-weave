using KyberWeave.Application.Doctor;
using KyberWeave.Application.Arbiter;
using KyberWeave.Application.Hooks;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Mcp;
using KyberWeave.Application.Review;
using KyberWeave.Application.RunStart;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Application.Agents;
using KyberWeave.Application.Skills;
using KyberWeave.Application.Utilities;
using KyberWeave.Application.Squad;
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
        services.TryAddSingleton<IRunStartService, RunStartService>();
        services.TryAddSingleton<ISquadBriefService, SquadBriefService>();
        services.TryAddSingleton<IManagedGlossaryFactory>(sp => new ManagedGlossaryFactory(
            sp.GetRequiredService<IRepositoryFileStore>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IPlanTaskService>(sp => new PlanTaskService(
            sp.GetRequiredService<IRepositoryFileStore>(), sp.GetRequiredService<IBoundedProcessExecutor>()));
        services.TryAddSingleton<IStatusLineDeploymentService, StatusLineDeploymentService>();
        services.TryAddSingleton<IStatusLineHostService, StatusLineHostService>();
        services.TryAddSingleton<ISkillGovernanceService, SkillGovernanceService>();
        services.TryAddSingleton<IAgentGovernanceService, AgentGovernanceService>();
        services.TryAddSingleton<ICodeGraphProvisioner, CodeGraphIndexProvisioner>();
        services.TryAddSingleton<IWorktreeIndexGuard, WorktreeIndexEnsurer>();
        services.TryAddSingleton<McpServerLiveProbe>();
        services.TryAddSingleton<PostCheckoutHookInstaller>();
        services.TryAddSingleton<IRepositoryDoctor, RepositoryDoctorService>();
        services.TryAddSingleton(sp => new McpWiringInspector(sp.GetRequiredService<IMcpConfigFiles>()));
        services.TryAddSingleton(sp => new McpWiringRepairer(sp.GetRequiredService<IMcpConfigFiles>()));
        services.TryAddSingleton(sp => new McpServerRenamer(sp.GetRequiredService<IMcpConfigFiles>(), sp.GetRequiredService<IMcpDeclarationReader>()));
        services.TryAddSingleton(sp => new McpWiringFixes(sp.GetRequiredService<IMcpConfigFiles>(), sp.GetRequiredService<McpServerRenamer>()));
        services.TryAddSingleton<IMcpWiringService>(sp => new McpWiringService(
            sp.GetRequiredService<McpWiringInspector>(),
            sp.GetRequiredService<McpWiringRepairer>(),
            sp.GetRequiredService<McpServerRenamer>(),
            sp.GetRequiredService<McpWiringFixes>(),
            sp.GetRequiredService<ILaunchCommandResolverFactory>()));
        services.TryAddSingleton<ICorpusProvenance>(sp => new CorpusProvenanceService(sp.GetRequiredService<IGitCheckoutProbe>()));
        services.TryAddSingleton<ISquadBriefService>(sp => new SquadBriefService(sp.GetRequiredService<IFileSystem>()));
        _ = services.AddWeaveSquadApplication();
        services.TryAddSingleton<IReviewEvidenceService, ReviewEvidenceService>();
        services.TryAddSingleton<IReviewWorkTreeService, ReviewWorkTreeService>();
        services.TryAddSingleton<ISuppressionScanService, SuppressionScanService>();
        services.TryAddSingleton<IGateRunService, GateRunService>();
        services.TryAddSingleton<ICitedLineChecker, CitedLineChecker>();
        services.TryAddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        _ = services.AddWeaveArbiterApplication();
        return services;
    }
}
