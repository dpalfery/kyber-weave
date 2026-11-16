using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Opens the documentation corpus of one repository, for the use cases that load it more than once.</summary>
public interface IDocumentSourceFactory
{
    /// <summary>The corpus under <paramref name="repoRoot"/> as <paramref name="ontology"/> scopes it.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="ontology">The documentation roots and catalog.</param>
    IDocumentSource Create(string repoRoot, OntologyConfig ontology);
}
