using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Data.Docs.Analysis.Embeddings;
using KyberWeave.Data.Docs.Closeout;
using KyberWeave.Data.Docs.FileSystem;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Data.Review;

namespace KyberWeave.Tests;

/// <summary>The physical ports the documentation engine's classes take, shared by the tests that run them over a temporary checkout.</summary>
internal static class DocsPorts
{
    internal static readonly IRepositoryFileStore Files = new PhysicalRepositoryFileStore();
    internal static readonly IBoundedProcessExecutor Process = new KernelBoundedProcessExecutor();

    internal static readonly IProcessRunner Runner = new KernelProcessRunner();

    internal static readonly IExclusiveFileWriter Exclusive = new ExclusiveFileWriter();

    internal static readonly IPathResolver Paths = new PhysicalPathResolver(Files);

    internal static readonly IProcessEnvironment Environment = new ProcessEnvironment();

    internal static readonly IWritableFileSystem Disk = new PhysicalFileSystem();

    /// <summary>The cache-safety check over the real disk and the real git.</summary>
    internal static readonly AnalysisCacheSafety CacheSafety = new(Disk, Paths, Environment, Process);

    /// <summary>Opens analysis caches over the real disk and the real <c>sqlite3</c>, for a test that runs them on a temporary checkout.</summary>
    internal static readonly IAnalysisPersistenceFactory AnalysisPersistence =
        new SqliteAnalysisPersistenceFactory(Disk, Paths, Environment, Process, CacheSafety);

    /// <summary>The CodeGraph index of a temporary checkout, read through the real <c>sqlite3</c> and git.</summary>
    /// <param name="repositoryRoot">The checkout root.</param>
    internal static CodeGraphResolverAdapter CodeGraph(string repositoryRoot) =>
        (CodeGraphResolverAdapter)new CodeGraphPortsFactory(Disk, Paths, Environment, Runner, Staleness).ForRepository(repositoryRoot).Resolver;

    /// <summary>The CodeGraph index at <paramref name="databasePath"/>, read through the real <c>sqlite3</c>.</summary>
    /// <param name="databasePath">The index file.</param>
    internal static CodeGraphResolverAdapter CodeGraphAt(string databasePath) =>
        new(databasePath, Disk, Paths, Environment, Runner, Staleness);

    private static readonly CodeGraphIndexStaleness Staleness = new(Disk, new ProcessProgramRunner());

    /// <summary>Opens the analysis cache of a temporary checkout over the real disk and the real <c>sqlite3</c>.</summary>
    /// <param name="repositoryRoot">The checkout root.</param>
    internal static SqliteAnalysisPersistence Sqlite(string repositoryRoot) =>
        new(repositoryRoot, Disk, Paths, Environment, Process, CacheSafety);

    /// <summary>Creates embedding clients over the real loopback transport; a test that sends nothing may use it.</summary>
    internal static readonly IEmbeddingGeneratorFactory Embeddings =
        new OpenAiCompatibleEmbeddingGeneratorFactory(new LoopbackEmbeddingTransport(), Environment, TimeProvider.System);

    internal static IPlanTaskService Tasks => new PlanTaskService(Files, Paths, Environment);

    /// <summary>The standards-template diff over the physical seams, for a test that runs it on a temporary checkout.</summary>
    internal static StandardsTemplateDiff StandardsDiff(string repoRoot, OntologyConfig ontology)
    {
        IFileSystem files = new PhysicalFileSystem();
        return new StandardsTemplateDiff(
            files, repoRoot, ontology, new PhysicalPathResolver(files), Environment, ShippedKyberStandards.Templates);
    }

    /// <summary>The standards migration over the physical seams, for a test that runs it on a temporary checkout.</summary>
    internal static StandardsFolderMigration StandardsMigration(string repoRoot, KyberWeaveConfig config)
    {
        IWritableFileSystem files = new PhysicalFileSystem();
        return new StandardsFolderMigration(
            files, repoRoot, config, new PhysicalPathResolver(files), Environment, new SystemIdGenerator());
    }
}
