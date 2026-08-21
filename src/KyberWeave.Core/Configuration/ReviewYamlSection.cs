namespace KyberWeave.Core.Configuration;

/// <summary>The <c>review:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class ReviewYamlSection
{
    public List<ReviewGateYaml>? Gates { get; set; }

    public ReviewCoverageYaml? Coverage { get; set; }

    public ReviewPolicyYaml? Policy { get; set; }
}

/// <summary>One entry of <c>review.gates</c>.</summary>
internal sealed class ReviewGateYaml
{
    public string? Id { get; set; }

    public List<string>? Run { get; set; }

    public bool? Blocking { get; set; }
}

/// <summary>The <c>review.coverage</c> mapping.</summary>
internal sealed class ReviewCoverageYaml
{
    public double? FileLinePercent { get; set; }

    public double? ClassLinePercent { get; set; }
}

/// <summary>The <c>review.policy</c> mapping.</summary>
internal sealed class ReviewPolicyYaml
{
    public List<string>? AlwaysHuman { get; set; }

    public int? MaxReviewableLines { get; set; }

    public int? MajorCountBlocks { get; set; }

    public int? MinConfidence { get; set; }

    public List<ReviewSuppressionYaml>? Suppressions { get; set; }
}

/// <summary>One entry of <c>review.policy.suppressions</c>.</summary>
internal sealed class ReviewSuppressionYaml
{
    public string? Id { get; set; }

    public string? Reason { get; set; }

    public string? Expires { get; set; }
}
