using System.Collections.Immutable;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>What duplicate detection compares, and the report for a run that had nothing to read.</summary>
public static class DuplicateDetector
{
    /// <summary>
    /// The CodeGraph node kinds whose bodies are worth comparing. Frozen so one caller
    /// cannot change what every other caller — and <c>IDuplicateDetectionService.Detect</c> itself — compares.
    /// </summary>
    public static ImmutableArray<string> ComparableKinds { get; } = ["method", "function"];

    /// <summary>The report for a run that found no index to read.</summary>
    public static DuplicateReport Unavailable(string indexPath, string reason, ReviewDuplicates options)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        ArgumentNullException.ThrowIfNull(options);

        return new DuplicateReport(
            DuplicateReport.CurrentSchema,
            indexPath,
            IndexAvailable: false,
            IndexModifiedUtc: null,
            reason,
            options.MinimumLines,
            SymbolsConsidered: 0,
            SymbolsUnreadable: 0,
            Clusters: []);
    }
}
