using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Docs.Closeout;
using KyberWeave.Data.Docs.FileSystem;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests;

/// <summary>The physical ports the documentation engine's classes take, shared by the tests that run them over a temporary checkout.</summary>
internal static class DocsPorts
{
    internal static readonly IRepositoryFileStore Files = new PhysicalRepositoryFileStore();
    internal static readonly IBoundedProcessExecutor Process = new KernelBoundedProcessExecutor();

    internal static readonly IExclusiveFileWriter Exclusive = new ExclusiveFileWriter();

    internal static readonly IPathResolver Paths = new PhysicalPathResolver(Files);

    internal static readonly IProcessEnvironment Environment = new ProcessEnvironment();

    internal static IPlanTaskService Tasks => new PlanTaskService(Files, Paths, Environment);

    /// <summary>The standards-template diff over the physical seams, for a test that runs it on a temporary checkout.</summary>
    internal static StandardsTemplateDiff StandardsDiff(string repoRoot, OntologyConfig ontology)
    {
        IFileSystem files = new PhysicalFileSystem();
        return new StandardsTemplateDiff(files, repoRoot, ontology, new PhysicalPathResolver(files), Environment);
    }

    /// <summary>The standards migration over the physical seams, for a test that runs it on a temporary checkout.</summary>
    internal static StandardsFolderMigration StandardsMigration(string repoRoot, OntologyConfig ontology)
    {
        IWritableFileSystem files = new PhysicalFileSystem();
        return new StandardsFolderMigration(
            files, repoRoot, ontology, new PhysicalPathResolver(files), Environment, new SystemIdGenerator());
    }
}
