using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Analysis;

namespace KyberWeave.Core.Docs.Analysis.Persistence;

/// <summary>Opens <see cref="SqliteAnalysisPersistence"/> caches over the registered file, path and process seams.</summary>
/// <param name="files">Creates the cache folder and inspects its path for links.</param>
/// <param name="paths">Resolves a repository root to a full path.</param>
/// <param name="environment">Supplies the directory a relative root resolves against.</param>
/// <param name="processes">Runs <c>sqlite3</c> under a time limit.</param>
/// <param name="safety">Decides whether a repository's cache is protected from accidental commits.</param>
public sealed class SqliteAnalysisPersistenceFactory(
    IWritableFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment,
    IBoundedProcessExecutor processes,
    AnalysisCacheSafety safety) : IAnalysisPersistenceFactory
{
    /// <inheritdoc />
    public bool IsCacheSafe(string repositoryRoot) => safety.IsSafe(repositoryRoot);

    /// <inheritdoc />
    public IAnalysisPersistence Open(string repositoryRoot) =>
        new SqliteAnalysisPersistence(repositoryRoot, files, paths, environment, processes, safety);
}
