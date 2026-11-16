using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Domain.CodeGraph;

namespace KyberWeave.Contracts.Services;

/// <summary>Measures declarations' bodies and reports the ones that repeat verbatim.</summary>
public interface IDuplicateDetectionService
{
    /// <summary>Clusters the given symbols by normalized body.</summary>
    /// <param name="repoRoot">The tree the index's relative paths resolve against.</param>
    /// <param name="symbols">The symbols to compare; kinds other than the comparable ones are ignored.</param>
    /// <param name="options">The host's duplicate-detection settings.</param>
    /// <param name="indexPath">The CodeGraph index the symbols came from.</param>
    /// <param name="indexModifiedUtc">When the index was last written, or null when unknown.</param>
    DuplicateReport Detect(
        string repoRoot,
        IReadOnlyList<CodeGraphNode> symbols,
        ReviewDuplicates options,
        string indexPath,
        DateTime? indexModifiedUtc);

    /// <summary>Reads the comparable symbols from the CodeGraph ports and clusters them, or reports why no index was read.</summary>
    /// <param name="repoRoot">The tree the index's relative paths resolve against.</param>
    /// <param name="graph">The resolver that says whether the index is available.</param>
    /// <param name="symbols">The enumerator the comparable symbols are read from.</param>
    /// <param name="options">The settings the host declared under <c>review.duplicates</c>.</param>
    DuplicateReport Analyze(
        string repoRoot,
        ICodeGraphResolver graph,
        ICodeGraphSymbolEnumerator symbols,
        ReviewDuplicates options);
}
