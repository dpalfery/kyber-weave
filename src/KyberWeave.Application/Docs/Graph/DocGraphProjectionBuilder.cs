using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>
/// Binds the CodeGraph ports to the pure <see cref="DocGraphProjection"/> so callers holding an
/// <see cref="ICodeGraphResolver"/> can build the shared projection.
/// </summary>
public static class DocGraphProjectionBuilder
{
    /// <summary>Builds the shared projection and optional one-hop CodeGraph neighborhood.</summary>
    public static DocGraphProjection Build(
        DocumentSet documents,
        ICodeGraphResolver codeGraph,
        int maxCodeNeighbors = 50,
        IReadOnlyList<IDocGraphContributor>? contributors = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(codeGraph);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCodeNeighbors);

        List<DocGraphContribution>? contributions = null;
        if (contributors is not null)
        {
            contributions = new List<DocGraphContribution>(contributors.Count);
            foreach (IDocGraphContributor contributor in contributors)
            {
                ArgumentNullException.ThrowIfNull(contributor);
                contributions.Add(contributor.Contribute(documents, codeGraph)
                                  ?? throw new InvalidOperationException(
                                      "A DocGraph contributor returned null."));
            }
        }

        return DocGraphProjection.Build(
            documents,
            codeGraph.ResolveSymbol,
            codeGraph.ResolveRoute,
            codeGraph is ICodeGraphNeighborhoodProvider neighborhoods ? neighborhoods.GetEdges : null,
            maxCodeNeighbors,
            contributions);
    }
}
