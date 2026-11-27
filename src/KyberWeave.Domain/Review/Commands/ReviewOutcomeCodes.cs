namespace KyberWeave.Core.Review;

/// <summary>Rule identifiers for individual gate outcomes.</summary>
public static class ReviewGateOutcome
{
    /// <summary>A gate that succeeded.</summary>
    public const string Passed = "KW-REVIEW-021";

    /// <summary>A gate that did not succeed.</summary>
    public const string Failed = "KW-REVIEW-022";

    /// <summary>A gate that does not apply to the change and was not executed.</summary>
    public const string NotApplicable = ReviewDiagnosticCodes.GateSkipped;
}

/// <summary>Rule identifiers for duplicate-detection outcomes.</summary>
public static class ReviewDuplicateOutcome
{
    /// <summary>No CodeGraph index was available, so the gate produced no evidence.</summary>
    public const string NoIndex = "KW-REVIEW-030";

    /// <summary>A set of symbols sharing one body.</summary>
    public const string Cluster = "KW-REVIEW-031";

    /// <summary>The index disagrees with the working tree.</summary>
    public const string StaleIndex = "KW-REVIEW-032";
}

/// <summary>Rule identifiers for the suppression scan.</summary>
public static class ReviewSuppressionOutcome
{
    /// <summary>A suppression the change added without stating a reason.</summary>
    public const string NoReason = "KW-REVIEW-036";

    /// <summary>The change could not be read, so no line was scanned.</summary>
    public const string ChangeUnreadable = "KW-REVIEW-037";
}
