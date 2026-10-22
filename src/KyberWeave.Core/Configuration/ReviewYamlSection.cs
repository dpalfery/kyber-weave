namespace KyberWeave.Core.Configuration;

/// <summary>The <c>review:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class ReviewYamlSection
{
    public List<ReviewGateYaml>? Gates { get; init; }

    /// <summary>Agent slug to its ordered task gates, as the operator wrote them.</summary>
    public Dictionary<string, List<ReviewTaskGateYaml>?>? TaskGates { get; init; }

    public ReviewCoverageYaml? Coverage { get; init; }

    public ReviewDuplicatesYaml? Duplicates { get; init; }

    public ReviewPolicyYaml? Policy { get; init; }
}

/// <summary>The <c>review.duplicates</c> mapping.</summary>
internal sealed class ReviewDuplicatesYaml
{
    public int? MinimumLines { get; init; }
}

/// <summary>One entry of <c>review.gates</c>.</summary>
internal sealed class ReviewGateYaml
{
    public string? Id { get; init; }

    public List<string>? Run { get; init; }

    public bool? Blocking { get; init; }

    public int? TimeoutSeconds { get; init; }
}

/// <summary>One entry of <c>review.task-gates.&lt;agent&gt;</c>.</summary>
internal sealed class ReviewTaskGateYaml
{
    public string? Id { get; init; }

    public List<string>? Include { get; init; }

    public string? Os { get; init; }

    public List<string>? Run { get; init; }

    public List<string>? Fix { get; init; }

    public bool? Blocking { get; init; }

    public int? Timeout { get; init; }
}

/// <summary>The <c>review.coverage</c> mapping.</summary>
internal sealed class ReviewCoverageYaml
{
    public double? FileLinePercent { get; init; }

    public double? ClassLinePercent { get; init; }
}

/// <summary>The <c>review.policy</c> mapping.</summary>
internal sealed class ReviewPolicyYaml
{
    public List<string>? AlwaysHuman { get; init; }

    public int? MaxReviewableLines { get; init; }

    public int? MajorCountBlocks { get; init; }

    public int? MinConfidence { get; init; }

    public List<ReviewSuppressionYaml>? Suppressions { get; init; }
}

/// <summary>One entry of <c>review.policy.suppressions</c>.</summary>
internal sealed class ReviewSuppressionYaml
{
    public string? Id { get; init; }

    public string? Reason { get; init; }

    public string? Expires { get; init; }
}
