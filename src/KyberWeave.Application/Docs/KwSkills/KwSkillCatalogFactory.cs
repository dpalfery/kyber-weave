using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>Builds the KW skill catalog for one checkout.</summary>
/// <param name="files">Reads the repository pool and writes a shipped skill's copy.</param>
/// <param name="paths">Resolves and contains paths.</param>
/// <param name="environment">Names the working directory and the home folder.</param>
/// <param name="resources">Holds the shipped pool.</param>
public sealed class KwSkillCatalogFactory(
    IWritableFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment,
    IEmbeddedResources resources) : IKwSkillCatalogFactory
{
    /// <inheritdoc />
    public IKwSkillCatalog ForRepository(string repositoryRoot, KyberWeaveConfig config) =>
        new KwSkillCatalog(files, paths, environment, resources, repositoryRoot, config);
}
