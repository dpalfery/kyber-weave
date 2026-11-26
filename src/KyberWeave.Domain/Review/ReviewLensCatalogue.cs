namespace KyberWeave.Core.Review;

/// <summary>The review lenses a council seat can run, by name.</summary>
public static class ReviewLensCatalogue
{
    /// <summary>The lens name <c>authz-tenancy</c>.</summary>
    public const string AuthzTenancy = "authz-tenancy";

    /// <summary>The lens name <c>blast-radius-revertibility</c>.</summary>
    public const string BlastRadiusRevertibility = "blast-radius-revertibility";

    /// <summary>The lens name <c>correctness</c>.</summary>
    public const string Correctness = "correctness";

    /// <summary>The lens name <c>dependency-supply-chain</c>.</summary>
    public const string DependencySupplyChain = "dependency-supply-chain";

    /// <summary>The lens name <c>di-composition</c>.</summary>
    public const string DiComposition = "di-composition";

    /// <summary>The lens name <c>documentation-accuracy</c>.</summary>
    public const string DocumentationAccuracy = "documentation-accuracy";

    /// <summary>The lens name <c>duplicate-implementation</c>.</summary>
    public const string DuplicateImplementation = "duplicate-implementation";

    /// <summary>The lens name <c>infra-workflow</c>.</summary>
    public const string InfraWorkflow = "infra-workflow";

    /// <summary>The lens name <c>intent-alignment</c>.</summary>
    public const string IntentAlignment = "intent-alignment";

    /// <summary>The lens name <c>model-placement</c>.</summary>
    public const string ModelPlacement = "model-placement";

    /// <summary>The lens name <c>performance</c>.</summary>
    public const string Performance = "performance";

    /// <summary>The lens name <c>prior-art</c>.</summary>
    public const string PriorArt = "prior-art";

    /// <summary>The lens name <c>security</c>.</summary>
    public const string Security = "security";

    /// <summary>The lens name <c>static-analysis-triage</c>.</summary>
    public const string StaticAnalysisTriage = "static-analysis-triage";

    /// <summary>The lens name <c>supportability</c>.</summary>
    public const string Supportability = "supportability";

    /// <summary>The lens name <c>test-adequacy</c>.</summary>
    public const string TestAdequacy = "test-adequacy";

    /// <summary>The lens name <c>test-infrastructure</c>.</summary>
    public const string TestInfrastructure = "test-infrastructure";

    /// <summary>The lens names, in ordinal order.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        AuthzTenancy,
        BlastRadiusRevertibility,
        Correctness,
        DependencySupplyChain,
        DiComposition,
        DocumentationAccuracy,
        DuplicateImplementation,
        InfraWorkflow,
        IntentAlignment,
        ModelPlacement,
        Performance,
        PriorArt,
        Security,
        StaticAnalysisTriage,
        Supportability,
        TestAdequacy,
        TestInfrastructure
    ];
}
