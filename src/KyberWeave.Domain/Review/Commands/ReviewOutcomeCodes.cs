namespace KyberWeave.Cli.Commands.Review;

/// <summary>Rule identifiers for individual gate outcomes.</summary>
public static class ReviewGateOutcome
{
    /// <summary>A gate that succeeded.</summary>
    public const string Passed = "KW-REVIEW-021";

    /// <summary>A gate that did not succeed.</summary>
    public const string Failed = "KW-REVIEW-022";

    /// <summary>A gate that does not apply to the change and was not executed.</summary>
    public const string NotApplicable = "KW-REVIEW-026";
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
