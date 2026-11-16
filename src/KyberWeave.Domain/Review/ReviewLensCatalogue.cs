namespace KyberWeave.Core.Review;

/// <summary>The review lenses a council seat can run, by name.</summary>
public static class ReviewLensCatalogue
{
    /// <summary>The lens names, in ordinal order.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "authz-tenancy",
        "blast-radius-revertibility",
        "correctness",
        "dependency-supply-chain",
        "di-composition",
        "documentation-accuracy",
        "duplicate-implementation",
        "infra-workflow",
        "intent-alignment",
        "model-placement",
        "performance",
        "prior-art",
        "security",
        "static-analysis-triage",
        "supportability",
        "test-adequacy",
        "test-infrastructure"
    ];
}
