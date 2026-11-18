using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.Docs.FileSystem;
using KyberWeave.Data.Docs.Closeout;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Core.Doctor;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Docs.Mcp;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Doctor;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Review;
using KyberWeave.Data.Skills;
using KyberWeave.Data.RunStart;
using KyberWeave.Data.Utilities;
using KyberWeave.Data.Squad;
using KyberWeave.Kernel.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Data;

/// <summary>Composition root for the data layer.</summary>
/// <remarks>Every registration uses <c>TryAdd</c>, so a host or test that registers its own implementation first keeps it.</remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the adapters that implement the contracts.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddWeaveDataServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddWeaveRunData();
        services.TryAddSingleton<IProductRootLocator>(sp => new CheckoutProductRootLocator(sp.GetRequiredService<IWritableFileSystem>()));
        services.TryAddSingleton<IRepositoryFileStore>(_ => new PhysicalRepositoryFileStore());
        services.TryAddSingleton<IBoundedProcessExecutor>(_ => new KernelBoundedProcessExecutor());
        services.TryAddSingleton<IExclusiveFileWriter>(_ => new ExclusiveFileWriter());
        services.TryAddSingleton<IDocumentSourceFactory>(_ => new DocumentLoaderFactory());
        services.TryAddSingleton<IGitWorktreeIdentity>(_ => new GitWorktreeIdentity());
        services.TryAddSingleton<IDoctorFileSystem>(_ => new PhysicalDoctorFileSystem());
        services.TryAddSingleton<ICodeGraphPortsFactory, CodeGraphPortsFactory>();
        services.TryAddSingleton<ICodeGraphIndexWorkspace, FileCodeGraphIndexWorkspace>();
        services.TryAddSingleton<IGitHookFiles, FileGitHookFiles>();
        services.TryAddSingleton<IMcpStdioSessionFactory, StdioMcpSessionFactory>();
        services.TryAddSingleton<IStatusLineFiles, FileStatusLineFiles>();
        services.TryAddSingleton(_ => StatusLineVersionProbeRunner.Create());
        services.TryAddSingleton<IUserHostConfigStore, FileUserHostConfigStore>();
        services.TryAddSingleton<IGitCheckoutProbe>(_ => new GitCheckoutProbe());
        services.TryAddSingleton<IMcpConfigFiles>(_ => new PhysicalMcpConfigFiles());
        services.TryAddSingleton<ILaunchCommandResolverFactory>(_ => new KyberWeave.Core.Docs.Mcp.EnvironmentLaunchCommandResolverFactory());
        _ = services.AddWeaveSquadData();
        services.TryAddSingleton<IProgramRunner>(_ => new ProcessProgramRunner());
        services.TryAddSingleton<IGitChangeReader>(sp => new GitChangeReader(sp.GetRequiredService<IProgramRunner>()));
        services.TryAddSingleton<IReviewCycleStore>(_ => new FileReviewCycleStore());
        services.TryAddSingleton<ISeatFolder, FileSeatFolder>();
        services.TryAddSingleton<ISkillArchiveWriter, ZipSkillArchiveWriter>();
        services.TryAddSingleton<INotSeatedStore>(_ => new FileNotSeatedStore());
        services.TryAddSingleton<IReviewEvidenceStore>(_ => new FileReviewEvidenceStore());
        services.TryAddSingleton<ISeverityOverrideSource>(_ => new FileSeverityOverrideSource());
        services.TryAddSingleton<IProjectFileLocator>(_ => new FileProjectFileLocator());
        services.TryAddSingleton<IRepositoryFileLister>(sp => new FileRepositoryFileLister(sp.GetRequiredService<IProgramRunner>()));
        services.TryAddSingleton<ICoverageReportSource>(_ => new FileCoverageReportSource());
        services.TryAddSingleton<IContainedFileReader>(_ => new FileContainedFileReader());
        services.TryAddSingleton<IBoundedFileReader>(_ => new FileBoundedFileReader());
        services.TryAddSingleton<IReviewDecisionCacheStore>(_ => new FileReviewDecisionCacheStore());
        services.TryAddSingleton<ICitedSourceFiles>(_ => new FileCitedSourceFiles());
        services.TryAddSingleton<IRepositoryTreeProbe>(_ => new FileRepositoryTreeProbe());
        services.TryAddSingleton<ITestClassSources>(_ => new FileTestClassSources());
        services.TryAddSingleton<IKyberWeaveConfigRepository>(sp => new FileKyberWeaveConfigRepository(sp.GetRequiredService<IRepositoryTreeProbe>()));
        services.TryAddSingleton<IRepositoryToolSettings>(sp => new ConfigFileToolSettings(sp.GetRequiredService<IKyberWeaveConfigRepository>()));
        _ = services.AddWeaveArbiterData();
        return services;
    }

    /// <summary>
    /// Registers the adapters a hook process needs on every event: the file system, the process, git and the run records.
    /// A host that needs only these (the Arbiter guards, which run on every tool call) registers them alone and skips the rest.
    /// </summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddWeaveRunData(this IServiceCollection services)
    {
        services.TryAddSingleton<IWritableFileSystem>(_ => new PhysicalFileSystem());
        services.TryAddSingleton<IFileSystem>(sp => sp.GetRequiredService<IWritableFileSystem>());
        services.TryAddSingleton<IProcessEnvironment>(_ => new ProcessEnvironment());
        services.TryAddSingleton<IProcessRunner>(_ => new KernelProcessRunner());
        services.TryAddSingleton<IIdGenerator>(_ => new SystemIdGenerator());
        services.TryAddSingleton<IGitCommandRunner>(_ => new ProcessGitCommandRunner(ProcessGitCommandRunner.DefaultTimeout));
        services.TryAddSingleton<ICheckedOutBranchReader>(sp => new GitCheckedOutBranchReader(sp.GetRequiredService<IGitCommandRunner>()));
        services.TryAddSingleton<IRunStateStore>(sp => new FileRunStateStore(sp.GetRequiredService<IWritableFileSystem>(), sp.GetRequiredService<ICheckedOutBranchReader>()));
        services.TryAddSingleton<IRunCheckout>(sp => new RunCheckoutGuard(sp.GetRequiredService<IWritableFileSystem>(), sp.GetRequiredService<IRunStateStore>()));
        return services;
    }
}
