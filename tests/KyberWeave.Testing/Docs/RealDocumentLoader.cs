using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Data.Docs.FileSystem;

namespace KyberWeave.Tests;

/// <summary>
/// Builds the real <see cref="DocumentLoader"/> over the real repository file store for the tests whose subject is
/// the corpus on disk, symbolic links included. The class takes its file seam, so a test that does not need the
/// disk gives it an in-memory store instead.
/// </summary>
internal static class RealDocumentLoader
{
    /// <summary>The loader over <paramref name="repoRoot"/> with the default documentation root.</summary>
    /// <param name="repoRoot">The checkout root.</param>
    internal static DocumentLoader Create(string repoRoot) => new(new PhysicalRepositoryFileStore(), repoRoot);

    /// <summary>The loader over <paramref name="repoRoot"/> with one documentation root.</summary>
    /// <param name="repoRoot">The checkout root.</param>
    /// <param name="docsRelativeRoot">The documentation root, relative to the checkout.</param>
    internal static DocumentLoader Create(string repoRoot, string docsRelativeRoot) =>
        new(new PhysicalRepositoryFileStore(), repoRoot, docsRelativeRoot);

    /// <summary>The loader over <paramref name="repoRoot"/> with the given ontology.</summary>
    /// <param name="repoRoot">The checkout root.</param>
    /// <param name="config">The ontology naming the roots, exclusions and catalog.</param>
    internal static DocumentLoader Create(string repoRoot, OntologyConfig config) =>
        new(new PhysicalRepositoryFileStore(), repoRoot, config);
}
