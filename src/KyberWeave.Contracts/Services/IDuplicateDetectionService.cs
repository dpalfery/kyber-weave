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
}
