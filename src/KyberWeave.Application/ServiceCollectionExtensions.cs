using KyberWeave.Application.Configuration;
using KyberWeave.Application.Doctor;
using KyberWeave.Application.Arbiter;
using KyberWeave.Application.Hooks;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Paths;
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
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
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
        _ = services.AddWeaveRunStartState();
        services.TryAddSingleton<IUserHostConfigStore>(sp => new FileUserHostConfigStore(sp.GetRequiredService<IWritableFileSystem>()));
        services.TryAddSingleton<IHooksConfigSource>(sp => new YamlHooksConfigSource(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ITestClassResolver>(sp => new ConventionTestClassResolver(sp.GetRequiredService<IFileSystem>()));
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
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IDocsSchemaGate>(sp => new DocsSchemaGate(
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IGitChangeReader>(),
            sp.GetRequiredService<ISeverityOverrideSource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
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
            sp.GetRequiredService<IFileSystemPathSemantics>(),
            sp.GetRequiredService<IPathResolver>()));
        services.TryAddSingleton<ISkillGovernanceService, SkillGovernanceService>();
        services.TryAddSingleton<IAgentGovernanceService, AgentGovernanceService>();
        services.TryAddSingleton<ICodeGraphProvisioner, CodeGraphIndexProvisioner>();
        services.TryAddSingleton<IWorktreeIndexGuard, WorktreeIndexEnsurer>();
        services.TryAddSingleton<McpServerLiveProbe>();
        services.TryAddSingleton<PostCheckoutHookInstaller>();
        services.TryAddSingleton<IRepositoryDoctor, RepositoryDoctorService>();
        services.TryAddSingleton(sp => new McpRepositoryPath(
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton(sp => new McpWiringInspector(
            sp.GetRequiredService<IMcpConfigFiles>(),
            sp.GetRequiredService<McpRepositoryPath>()));
        services.TryAddSingleton<IMcpWiringReceipts>(sp => new McpWiringReceiptStore(sp.GetRequiredService<IWritableFileSystem>()));
        services.TryAddSingleton(sp => new McpWiringRepairer(
            sp.GetRequiredService<IMcpConfigFiles>(),
            sp.GetRequiredService<IMcpWiringReceipts>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<McpRepositoryPath>()));
        services.TryAddSingleton(sp => new McpServerRenamer(
            sp.GetRequiredService<IMcpConfigFiles>(),
            sp.GetRequiredService<IMcpDeclarationReader>(),
            sp.GetRequiredService<IMcpWiringReceipts>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<McpRepositoryPath>()));
        services.TryAddSingleton(sp => new McpWiringFixes(
            sp.GetRequiredService<IMcpConfigFiles>(),
            sp.GetRequiredService<McpServerRenamer>(),
            sp.GetRequiredService<McpRepositoryPath>()));
        services.TryAddSingleton<IMcpWiringService>(sp => new McpWiringService(
            sp.GetRequiredService<McpWiringInspector>(),
            sp.GetRequiredService<McpWiringRepairer>(),
            sp.GetRequiredService<McpServerRenamer>(),
            sp.GetRequiredService<McpWiringFixes>(),
            sp.GetRequiredService<ILaunchCommandResolverFactory>()));
        services.TryAddSingleton<IDocsHostConfiguration, HostConfiguration>();
        services.TryAddSingleton<IDocumentSourceFactory>(sp => new DocumentLoaderFactory(
            sp.GetRequiredService<IRepositoryFileStore>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IGitWorktreeIdentity>(sp => new GitWorktreeIdentity(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IGitCheckoutProbe>(sp => new GitCheckoutProbe(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IPathResolver>()));
        services.TryAddSingleton<ICorpusProvenance>(sp => new CorpusProvenanceService(
            sp.GetRequiredService<IGitCheckoutProbe>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
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
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ISquadBriefService>(sp => new SquadBriefService(sp.GetRequiredService<IFileSystem>()));
        _ = services.AddWeaveSquadApplication();
        services.TryAddSingleton<IRepositoryTreeProbe>(sp => new FileRepositoryTreeProbe(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IKyberWeaveConfigRepository>(sp => new FileKyberWeaveConfigRepository(
            sp.GetRequiredService<IRepositoryTreeProbe>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<INotSeatedStore>(sp => new FileNotSeatedStore(sp.GetRequiredService<IWritableFileSystem>()));
        services.TryAddSingleton<IRepositoryFileLister>(sp => new FileRepositoryFileLister(
            sp.GetRequiredService<IProgramRunner>(), sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ITestClassSources>(sp => new FileTestClassSources(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IProcessEnvironment>(),
            sp.GetRequiredService<IPathResolver>()));
        services.TryAddSingleton<IGitChangeReader>(sp => new GitChangeReader(sp.GetRequiredService<IProgramRunner>()));
        services.TryAddSingleton<ICoverageReportSource>(sp => new FileCoverageReportSource(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<IContainedFileReader>(sp => new FileContainedFileReader(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ICitedSourceFiles>(sp => new FileCitedSourceFiles(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IContainedFileReader>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<ISeverityOverrideSource>(sp => new FileSeverityOverrideSource(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IReviewCycleStore>(sp => new FileReviewCycleStore(
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
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
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IReviewDuplicatesService>(sp => new ReviewDuplicatesService(
            sp.GetRequiredService<IDuplicateDetectionService>(),
            sp.GetRequiredService<ICodeGraphPortsFactory>(),
            sp.GetRequiredService<IConfigLoadService>(),
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IReviewSuppressionsService>(sp => new ReviewSuppressionsService(
            sp.GetRequiredService<ISuppressionScanService>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IReviewCycleService>(sp => new ReviewCycleService(
            sp.GetRequiredService<IReviewWorkTreeService>(),
            sp.GetRequiredService<IReviewCycleStore>(),
            sp.GetRequiredService<IReviewCycleSelector>(),
            sp.GetRequiredService<IConfigLoadService>(),
            sp.GetRequiredService<IProcessEnvironment>()));
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
            sp.GetRequiredService<IPathResolver>(),
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
