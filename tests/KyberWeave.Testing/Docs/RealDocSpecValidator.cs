using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Docs.FileSystem;

namespace KyberWeave.Tests;

/// <summary>
/// Builds the real <see cref="DocSpecValidator"/> over the real repository file store for the tests whose subject
/// is the checkout on disk, symbolic links included. The class takes its file seam, so a test that does not need
/// the disk gives it an in-memory file system instead.
/// </summary>
internal static class RealDocSpecValidator
{
    /// <summary>The validator over <paramref name="repoRoot"/> with the product-default ontology.</summary>
    /// <param name="repoRoot">The checkout root.</param>
    internal static DocSpecValidator Create(string repoRoot) => Create(repoRoot, OntologyConfig.ProductDefaults);

    /// <summary>The validator over <paramref name="repoRoot"/> with the given ontology.</summary>
    /// <param name="repoRoot">The checkout root.</param>
    /// <param name="config">The ontology the documents are checked against.</param>
    internal static DocSpecValidator Create(string repoRoot, OntologyConfig config) =>
        new(repoRoot, config, new PhysicalRepositoryFileStore());
}
