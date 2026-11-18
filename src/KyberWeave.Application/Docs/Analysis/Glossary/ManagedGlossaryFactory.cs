using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Analysis.Glossary;

/// <summary>Opens the managed glossary of a repository over the checkout's file system.</summary>
/// <param name="files">Reads and writes the glossary and reads the catalog.</param>
/// <param name="timeProvider">Dates new rows.</param>
/// <param name="paths">Resolves the repository root and the glossary path to full paths.</param>
/// <param name="environment">Names the directory a relative repository root is resolved against.</param>
public sealed class ManagedGlossaryFactory(
    IRepositoryFileStore files,
    TimeProvider timeProvider,
    IPathResolver paths,
    IProcessEnvironment environment) : IManagedGlossaryFactory
{
    /// <inheritdoc />
    public IManagedGlossary Open(string repositoryRoot, KyberWeaveConfig config) =>
        new ManagedGlossary(repositoryRoot, config, timeProvider, files, paths, environment);
}
