using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>Opens the corpus with <see cref="DocumentLoader"/>.</summary>
public sealed class DocumentLoaderFactory : IDocumentSourceFactory
{
    /// <inheritdoc />
    public IDocumentSource Create(string repoRoot, OntologyConfig ontology) => new DocumentLoader(repoRoot, ontology);
}
