namespace KyberWeave.Core.Docs.Delivery;

public sealed record PullRequestContent
{
    /// <summary>The problem and the outcome, in one to three sentences.</summary>
    public required string Summary { get; init; }

    /// <summary>The kind of change, in words: bug fix, feature, refactor and so on.</summary>
    public string? Type { get; init; }

    /// <summary>The issue or work item, or null when there is none.</summary>
    public string? LinkedWork { get; init; }

    /// <summary>What changed, one short line each; empty when nothing recorded it.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>The evidence, as short readable lines; empty when the run recorded none.</summary>
    public IReadOnlyList<string> Validation { get; init; } = [];

    /// <summary>Every check that did not run or could not be confirmed, with its reason.</summary>
    public IReadOnlyList<string> NotVerified { get; init; } = [];

    /// <summary>Decisions made without a person that rest on no authoritative source; empty leaves the section out.</summary>
    public IReadOnlyList<string> Decisions { get; init; } = [];

    /// <summary>Review findings still open; empty leaves the section out.</summary>
    public IReadOnlyList<string> PendingReview { get; init; } = [];

    /// <summary>The todos filed for work found outside the change's scope, one line each; empty leaves the section out.</summary>
    public IReadOnlyList<string> FollowUps { get; init; } = [];

    /// <summary>The documentation-impact outcome, or null when the run did not record one.</summary>
    public string? DocumentationImpact { get; init; }

    /// <summary>Anything else reviewers should know, or null.</summary>
    public string? Notes { get; init; }
}
