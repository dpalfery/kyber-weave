using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>Opens the corpus with <see cref="DocumentLoader"/> over the repository file store.</summary>
public sealed class DocumentLoaderFactory(IRepositoryFileStore files) : IDocumentSourceFactory
{
    /// <inheritdoc />
    public IDocumentSource Create(string repoRoot, OntologyConfig ontology) => new DocumentLoader(files, repoRoot, ontology);
}
