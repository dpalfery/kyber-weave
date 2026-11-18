using KyberWeave.Domain.FileSystem;

namespace KyberWeave.Core.Configuration;

/// <summary>Replaces the documentation roots of an <see cref="OntologyConfig"/>.</summary>
public static class OntologyConfigRoots
{
    /// <summary>Replaces the configured roots with one.</summary>
    public static OntologyConfig WithDocsRoot(this OntologyConfig config, string docsRoot)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(docsRoot);
        return config.WithDocsRoots([docsRoot]);
    }

    /// <summary>
    /// Replaces the configured roots. Paths are canonicalized and checked for containment
    /// here as well as at load, so an operator-supplied <c>--docs-root</c> cannot reach a
    /// tree that the same value in <c>kyber-weave.yml</c> would have been refused.
    /// </summary>
    public static OntologyConfig WithDocsRoots(this OntologyConfig config, IReadOnlyList<string> docsRoots)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(docsRoots);
        return config.Clone(docsRoots: DocsRootPath.NormalizeRoots(docsRoots, "docs-root"));
    }
}
