namespace KyberWeave.Core.Arbiter;

/// <summary>The one spelling of every return status word the Arbiter renders or parses.</summary>
public static class ReturnStatus
{
    /// <summary>The marker that opens a status line.</summary>
    public const string Prefix = "STATUS:";

    /// <summary>The status of an escalation envelope.</summary>
    public const string ArbiterEscalation = "ARBITER_ESCALATION";

    /// <summary>The status of a lens denial note.</summary>
    public const string ArbiterSkip = "ARBITER_SKIP";

    /// <summary>The status of a refutation denial note.</summary>
    public const string ArbiterVerified = "ARBITER_VERIFIED";

    /// <summary>The status of a post-dispatch annotation note.</summary>
    public const string ArbiterAnnotation = "ARBITER_ANNOTATION";

    /// <summary>The status an architect returns when its plan is ready.</summary>
    public const string PlanReady = "PLAN_READY";

    /// <summary>The status a worker returns when its budget ran out.</summary>
    public const string BudgetExceeded = "BUDGET_EXCEEDED";

    /// <summary>A passing gate or task.</summary>
    public const string Pass = "PASS";

    /// <summary>A failing gate or task.</summary>
    public const string Fail = "FAIL";

    /// <summary>A finished worker's task status.</summary>
    public const string ReadyForReview = "READY_FOR_REVIEW";

    /// <summary>The task gate found no declared gates.</summary>
    public const string NoneDeclared = "NONE_DECLARED";

    /// <summary>The task gate found every declared gate not applicable.</summary>
    public const string NoneApplicable = "NONE_APPLICABLE";

    /// <summary>The marker that opens a task-gate digest line.</summary>
    public const string TaskGate = "TASK_GATE:";

    /// <summary>The escalation envelope's opening line.</summary>
    public const string EscalationLine = Prefix + " " + ArbiterEscalation;

    /// <summary>A lens denial note's opening line.</summary>
    public const string SkipLine = Prefix + " " + ArbiterSkip;

    /// <summary>A refutation denial note's opening line.</summary>
    public const string VerifiedLine = Prefix + " " + ArbiterVerified;

    /// <summary>A post-dispatch annotation note's opening line.</summary>
    public const string AnnotationLine = Prefix + " " + ArbiterAnnotation;

    /// <summary>Every status word a return carries and the code parses.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ArbiterEscalation,
        ArbiterSkip,
        ArbiterVerified,
        ArbiterAnnotation,
        PlanReady,
        BudgetExceeded,
        Pass,
        Fail,
        ReadyForReview,
        NoneDeclared,
        NoneApplicable,
    ];
}
