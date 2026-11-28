using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.Docs.FileSystem;
using KyberWeave.Data.Docs.Closeout;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Data.Docs.Analysis.Embeddings;
using KyberWeave.Core.Doctor;
using KyberWeave.Data.Docs.Mcp;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Doctor;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
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
        services.TryAddSingleton(TimeProvider.System);
        RegisterFileSystemAdapters(services);
        RegisterCodeGraphAndAnalysisAdapters(services);
        RegisterMcpAdapters(services);
        _ = services.AddWeaveSquadData();
        RegisterReviewAndSkillAdapters(services);
        _ = services.AddWeaveArbiterData();
        return services;
    }

    /// <summary>Registers the file system, process and host locator adapters.</summary>
    /// <param name="services">The collection to populate.</param>
    private static void RegisterFileSystemAdapters(IServiceCollection services)
    {
        services.TryAddSingleton<IProductRootLocator>(sp => new CheckoutProductRootLocator(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IRepositoryFileStore>(_ => new PhysicalRepositoryFileStore());
        services.TryAddSingleton<IBoundedProcessExecutor>(_ => new KernelBoundedProcessExecutor());
        services.TryAddSingleton<IExclusiveFileWriter>(_ => new ExclusiveFileWriter());
        services.TryAddSingleton<IDoctorFileSystem>(_ => new PhysicalDoctorFileSystem());
    }

    /// <summary>Registers the code graph, analysis and embedding adapters.</summary>
    /// <param name="services">The collection to populate.</param>
    private static void RegisterCodeGraphAndAnalysisAdapters(IServiceCollection services)
    {
        services.TryAddSingleton(sp => new CodeGraphIndexStaleness(
            sp.GetRequiredService<IFileSystem>(),
            // IProcessRunner: the staleness rule's async read needs it, and it is also the IProgramRunner view.
            sp.GetRequiredService<IProcessRunner>()));
        services.TryAddSingleton<ICodeGraphPortsFactory, CodeGraphPortsFactory>();
        services.TryAddSingleton<ICodeGraphIndexWorkspace, FileCodeGraphIndexWorkspace>();
        services.TryAddSingleton(sp => new AnalysisCacheSafety(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>(),
            // IProcessRunner: the async safety check needs it, and it is also the IBoundedProcessExecutor view.
            sp.GetRequiredService<IProcessRunner>()));
        services.TryAddSingleton<IAnalysisPersistenceFactory, SqliteAnalysisPersistenceFactory>();
        services.TryAddSingleton<IEmbeddingTransport, LoopbackEmbeddingTransport>();
        services.TryAddSingleton<IEmbeddingGeneratorFactory, OpenAiCompatibleEmbeddingGeneratorFactory>();
    }

    /// <summary>Registers the hook, MCP, status line and launch command adapters.</summary>
    /// <param name="services">The collection to populate.</param>
    private static void RegisterMcpAdapters(IServiceCollection services)
    {
        services.TryAddSingleton<IGitHookFiles, FileGitHookFiles>();
        services.TryAddSingleton<IMcpStdioSessionFactory, StdioMcpSessionFactory>();
        services.TryAddSingleton<IStatusLineFiles, FileStatusLineFiles>();
        services.TryAddSingleton(_ => StatusLineVersionProbeRunner.Create());
        services.TryAddSingleton<IMcpConfigFiles>(_ => new PhysicalMcpConfigFiles());
        services.TryAddSingleton<ILaunchCommandResolverFactory>(sp => new KyberWeave.Core.Docs.Mcp.EnvironmentLaunchCommandResolverFactory(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
    }

    /// <summary>Registers the review, skill and project file adapters.</summary>
    /// <param name="services">The collection to populate.</param>
    private static void RegisterReviewAndSkillAdapters(IServiceCollection services)
    {
        services.TryAddSingleton<IProgramRunner>(_ => new ProcessProgramRunner());
        services.TryAddSingleton<ISeatFolder, FileSeatFolder>();
        services.TryAddSingleton<ISkillArchiveWriter, ZipSkillArchiveWriter>();
        services.TryAddSingleton<IReviewEvidenceStore>(sp => new FileReviewEvidenceStore(sp.GetRequiredService<IWritableFileSystem>()));
        services.TryAddSingleton<IProjectFileLocator>(sp => new FileProjectFileLocator(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IBoundedFileReader>(_ => new FileBoundedFileReader());
        services.TryAddSingleton<IReviewDecisionCacheStore>(sp => new FileReviewDecisionCacheStore(
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IRepositoryToolSettings>(sp => new ConfigFileToolSettings(sp.GetRequiredService<IKyberWeaveConfigRepository>()));
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
        services.TryAddSingleton<IPathResolver>(sp => new PhysicalPathResolver(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<IProcessEnvironment>(_ => new ProcessEnvironment());
        services.TryAddSingleton<IProcessRunner>(_ => new KernelProcessRunner());
        services.TryAddSingleton<IIdGenerator>(_ => new SystemIdGenerator());
        services.TryAddSingleton<IConsole>(_ => new SystemConsole());
        services.TryAddSingleton<IGitCommandRunner>(_ => new ProcessGitCommandRunner(ProcessGitCommandRunner.DefaultTimeout));
        services.TryAddSingleton<ICheckedOutBranchReader>(sp => new GitCheckedOutBranchReader(sp.GetRequiredService<IGitCommandRunner>()));
        services.TryAddSingleton<IArbiterLogStore>(_ => new KyberWeave.Core.Arbiter.Facts.ArbiterLogFiles());
        return services;
    }
}
