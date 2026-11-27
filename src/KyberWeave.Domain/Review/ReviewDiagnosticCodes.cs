namespace KyberWeave.Core.Review;

/// <summary>The diagnostics the review commands report, in one place so a code has one definition.</summary>
public static class ReviewDiagnosticCodes
{
    /// <summary>An input a review command could not read, including a prior cycle's findings.</summary>
    public const string UnreadableInput = "KW-REVIEW-023";

    /// <summary>A report file that could not be written.</summary>
    public const string ReportWriteFailed = "KW-REVIEW-025";

    /// <summary>A gate left out because it does not apply to the change.</summary>
    public const string GateSkipped = "KW-REVIEW-026";

    /// <summary>An input refused before any gate started.</summary>
    public const string InvalidInput = "KW-REVIEW-028";

    /// <summary>The state of the CodeGraph index a review runs over.</summary>
    public const string IndexState = "KW-REVIEW-038";

    /// <summary>A seat that reported no discovery evidence.</summary>
    public const string DiscoveryGap = "KW-REVIEW-044";

    /// <summary>The refutation candidates read from a seat file.</summary>
    public const string RefutationCandidates = "KW-REVIEW-046";

    /// <summary>A gate whose earlier passing result this cycle carries.</summary>
    public const string GateCarried = "KW-REVIEW-048";
}
