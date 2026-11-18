using KyberWeave.Application.Configuration;
using KyberWeave.Application.Doctor;
using KyberWeave.Application.Arbiter;
using KyberWeave.Application.Hooks;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Mcp;
using KyberWeave.Application.Review;
using KyberWeave.Application.RunStart;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Arbiter;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Application.Agents;
using KyberWeave.Application.Skills;
using KyberWeave.Application.Utilities;
using KyberWeave.Application.Squad;
using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data;
using KyberWeave.Data.Docs.Mcp;
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
        services.TryAddSingleton<IRunStartEvidence, RunStartEvidenceService>();
        services.TryAddSingleton<IRunStartService, RunStartService>();
        services.TryAddSingleton<ISquadBriefService, SquadBriefService>();
        services.TryAddSingleton<ISquadBriefBuilder, SquadBriefBuilder>();
        services.TryAddSingleton<IConfigLoadService, ConfigLoadService>();
        services.TryAddSingleton<IManagedGlossaryFactory>(sp => new ManagedGlossaryFactory(
            sp.GetRequiredService<IRepositoryFileStore>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IPlanTaskService>(sp => new PlanTaskService(
            sp.GetRequiredService<IRepositoryFileStore>()));
        services.TryAddSingleton<IDocsSchemaGate>(sp => new DocsSchemaGate(
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IGitChangeReader>(),
            sp.GetRequiredService<ISeverityOverrideSource>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IPacketHostFacts>(sp => new PacketHostFacts(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ITaskPromptServiceFactory>(sp => new TaskPromptServiceFactory(
            sp.GetRequiredService<IPlanTaskService>(),
            sp.GetRequiredService<IPacketHostFacts>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<ICodeGraphPortsFactory>(),
            sp.GetRequiredService<IDocumentSourceFactory>()));
        services.TryAddSingleton<IPlanCloseoutService>(sp => new PlanCloseoutService(
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IBoundedProcessExecutor>(),
            sp.GetRequiredService<IExclusiveFileWriter>(),
            sp.GetRequiredService<IDocumentSourceFactory>()));
        services.TryAddSingleton<IPlanFinalizeService>(sp => new PlanFinalizeService(
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IBoundedProcessExecutor>(),
            sp.GetRequiredService<IDocsSchemaGate>(),
            sp.GetRequiredService<IDocumentSourceFactory>(),
            sp.GetRequiredService<IPlanTaskAgentPickerFactory>()));
        services.TryAddSingleton<IPlanTaskAgentPickerFactory>(sp => new ConfiguredPlanTaskAgentPickerFactory(
            sp.GetRequiredService<ConfiguredArbiterProviderFactory>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton(sp => new RunDeliveryPorts(
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IBoundedProcessExecutor>(),
            sp.GetRequiredService<IRunStateStore>(),
            sp.GetRequiredService<IReviewCycleStore>(),
            sp.GetRequiredService<IProcessEnvironment>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IRunDeliveryRunner, RunDeliveryRunner>();
        services.TryAddSingleton<IStatusLineDeploymentService>(sp => new StatusLineDeploymentService(
            sp.GetRequiredService<IStatusLineFiles>(),
            sp.GetRequiredService<IFileSystemPathSemantics>()));
        services.TryAddSingleton<IStatusLineHostService>(sp => new StatusLineHostService(
            sp.GetRequiredService<IStatusLineFiles>(),
            sp.GetRequiredService<IProcessEnvironment>(),
            sp.GetRequiredService<IProductRootLocator>(),
            sp.GetRequiredService<IFileSystemPathSemantics>()));
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
        services.TryAddSingleton<IDocsHostConfiguration, HostConfiguration>();
        services.TryAddSingleton<IDocumentSourceFactory>(sp => new DocumentLoaderFactory(sp.GetRequiredService<IRepositoryFileStore>()));
        services.TryAddSingleton<IGitWorktreeIdentity>(sp => new GitWorktreeIdentity(sp.GetRequiredService<IProcessRunner>()));
        services.TryAddSingleton<IGitCheckoutProbe>(sp => new GitCheckoutProbe(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IProcessRunner>()));
        services.TryAddSingleton<ICorpusProvenance>(sp => new CorpusProvenanceService(sp.GetRequiredService<IGitCheckoutProbe>()));
        services.TryAddSingleton(sp => new ReviewBlockWriter(
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IRepositoryFileLister>(),
            sp.GetRequiredService<IRepositoryTreeProbe>()));
        services.TryAddSingleton<IReviewGateCatalogue>(sp => sp.GetRequiredService<ReviewBlockWriter>());
        services.TryAddSingleton<IDocsScaffoldingService>(sp => new DocsScaffolder(
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IKyberWeaveConfigRepository>(),
            sp.GetRequiredService<IRepositoryFileLister>(),
            sp.GetRequiredService<IRepositoryTreeProbe>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ISquadBriefService>(sp => new SquadBriefService(sp.GetRequiredService<IFileSystem>()));
        _ = services.AddWeaveSquadApplication();
        services.TryAddSingleton<IReviewEvidenceService, ReviewEvidenceService>();
        services.TryAddSingleton<IReviewWorkTreeService, ReviewWorkTreeService>();
        services.TryAddSingleton<ISuppressionScanService, SuppressionScanService>();
        services.TryAddSingleton<IGateRunService, GateRunService>();
        services.TryAddSingleton<ICitedLineChecker, CitedLineChecker>();
        services.TryAddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        services.TryAddSingleton<IReviewDecisionSteps, ReviewDecisionSteps>();
        services.TryAddSingleton<ITaskGateSelectionService, TaskGateSelectionService>();
        services.TryAddSingleton<IConfigLoadService, ConfigLoadService>();
        services.TryAddSingleton<IReviewCycleSelector, ReviewCycleSelector>();
        services.TryAddSingleton<IReviewCatalogueService>(sp => new ReviewCatalogueService(
            sp.GetRequiredService<IConfigLoadService>(),
            sp.GetRequiredService<IRepositoryFileLister>(),
            sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<IReviewDuplicatesService>(sp => new ReviewDuplicatesService(
            sp.GetRequiredService<IDuplicateDetectionService>(),
            sp.GetRequiredService<ICodeGraphPortsFactory>(),
            sp.GetRequiredService<IConfigLoadService>(),
            sp.GetRequiredService<IWritableFileSystem>()));
        services.TryAddSingleton<IReviewSuppressionsService>(sp => new ReviewSuppressionsService(
            sp.GetRequiredService<ISuppressionScanService>(),
            sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<IReviewCycleService>(sp => new ReviewCycleService(
            sp.GetRequiredService<IReviewWorkTreeService>(),
            sp.GetRequiredService<IReviewCycleStore>(),
            sp.GetRequiredService<IReviewCycleSelector>(),
            sp.GetRequiredService<IConfigLoadService>()));
        services.TryAddSingleton<IReviewSeatsService>(sp => new ReviewSeatsService(
            sp.GetRequiredService<IReviewWorkTreeService>(),
            sp.GetRequiredService<IReviewCycleStore>(),
            sp.GetRequiredService<IGitChangeReader>(),
            sp.GetRequiredService<IReviewEvidenceService>(),
            sp.GetRequiredService<INotSeatedStore>(),
            sp.GetRequiredService<ISeverityOverrideSource>(),
            sp.GetRequiredService<IWorktreeIndexGuard>(),
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IProcessEnvironment>(),
            sp.GetRequiredService<IReviewCycleSelector>(),
            sp.GetRequiredService<IConfigLoadService>(),
            sp.GetRequiredService<IReviewDecisionSteps>()));
        services.TryAddSingleton<IReviewGatesService>(sp => new ReviewGatesService(
            sp.GetRequiredService<IReviewWorkTreeService>(),
            sp.GetRequiredService<IReviewCycleStore>(),
            sp.GetRequiredService<IGitChangeReader>(),
            sp.GetRequiredService<IReviewEvidenceService>(),
            sp.GetRequiredService<INotSeatedStore>(),
            sp.GetRequiredService<IProjectFileLocator>(),
            sp.GetRequiredService<IGateRunService>(),
            sp.GetRequiredService<IDuplicateDetectionService>(),
            sp.GetRequiredService<ICodeGraphPortsFactory>(),
            sp.GetRequiredService<IWorktreeIndexGuard>(),
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IProcessEnvironment>(),
            sp.GetRequiredService<IReviewCycleSelector>(),
            sp.GetRequiredService<IConfigLoadService>(),
            sp.GetRequiredService<ITaskGateSelectionService>(),
            sp.GetRequiredService<IPlanTaskTargetService>()));
        services.TryAddSingleton<IPlanTaskTargetService>(sp => new PlanTaskTargetService(
            sp.GetRequiredService<IPlanTaskService>(),
            sp.GetRequiredService<ITaskGateSelectionService>(),
            sp.GetRequiredService<IGitRepositoryFacts>()));
        services.TryAddSingleton<IReviewDecisionModelFactory, ReviewDecisionModelFactory>();
        services.TryAddSingleton<IReviewVerdictService, ReviewVerdictService>();
        _ = services.AddWeaveArbiterApplication();
        return services;
    }
}
