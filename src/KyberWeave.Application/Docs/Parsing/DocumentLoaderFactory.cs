using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>Opens the corpus with <see cref="DocumentLoader"/> over the repository file store.</summary>
public sealed class DocumentLoaderFactory(
    IRepositoryFileStore files,
    IPathResolver paths,
    IProcessEnvironment environment) : IDocumentSourceFactory
{
    /// <inheritdoc />
    public IDocumentSource Create(string repoRoot, OntologyConfig ontology) =>
        new DocumentLoader(files, repoRoot, paths, environment, ontology);
}
