using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.CodeGraph;

namespace KyberWeave.Data.CodeGraph;

/// <summary>Opens the CodeGraph ports over a checkout's index, one <c>sqlite3</c> read per call.</summary>
/// <param name="files">Tells whether an index exists.</param>
/// <param name="paths">Resolves a checkout root to a full path.</param>
/// <param name="environment">Supplies the directory a relative root resolves against.</param>
/// <param name="processes">Runs <c>sqlite3</c> under a time limit.</param>
/// <param name="staleness">Judges whether an index has fallen behind its checkout.</param>
public sealed class CodeGraphPortsFactory(
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment,
    IProcessRunner processes,
    CodeGraphIndexStaleness staleness) : ICodeGraphPortsFactory
{
    /// <inheritdoc />
    public CodeGraphPorts ForRepository(string repositoryRoot)
    {
        string databasePath = DatabasePathFor(repositoryRoot);
        CodeGraphResolverAdapter adapter = new(databasePath, files, paths, environment, processes, staleness);
        return new CodeGraphPorts(adapter, adapter, adapter, adapter);
    }

    /// <inheritdoc />
    public async Task<CodeGraphPorts> ForRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        string databasePath = DatabasePathFor(repositoryRoot);
        CodeGraphResolverAdapter adapter = await CodeGraphResolverAdapter.CreateAsync(
            databasePath,
            files,
            paths,
            environment,
            processes,
            staleness,
            cancellationToken).ConfigureAwait(false);
        return new CodeGraphPorts(adapter, adapter, adapter, adapter);
    }

    private string DatabasePathFor(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string root = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        return Path.Combine(root, ".codegraph", "codegraph.db");
    }
}
