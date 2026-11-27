namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>The one spelling of every dispatch header name and routing marker the Arbiter reads or writes.</summary>
public static class DispatchHeaders
{
    /// <summary>The routing marker that opens a dispatch header block.</summary>
    public const string Marker = "KYBER-ARBITER";

    /// <summary>The plan or specification file a dispatch belongs to.</summary>
    public const string PlanFile = "PLAN_FILE";

    /// <summary>The plan or specification task a dispatch executes.</summary>
    public const string Task = "TASK";

    /// <summary>The review lens a seat's dispatch runs.</summary>
    public const string Lens = "LENS";

    /// <summary>The finding a refutation seat's dispatch argues against.</summary>
    public const string Refute = "REFUTE";

    /// <summary>The review run whose findings a seat's dispatch reads.</summary>
    public const string ReviewRun = "REVIEW_RUN";

    /// <summary>The technology whose rules a task-less dispatch is built from.</summary>
    public const string Technology = "TECHNOLOGY";

    /// <summary>The retry's scratch file holding earlier results.</summary>
    public const string PriorResults = "PRIOR_RESULTS";

    /// <summary>The number of attempts at the task that already failed.</summary>
    public const string FailedAttempts = "FAILED_ATTEMPTS";

    /// <summary>The marker naming the run-start dispatch a conductor hands to a worker.</summary>
    public const string RunStart = "RUN-START";

    /// <summary>The marker naming a dispatch that needs a documentation-impact review.</summary>
    public const string DocsImpact = "DOCS-IMPACT";

    /// <summary>The marker naming the failure cluster a rework dispatch answers.</summary>
    public const string FailureCluster = "FAILURE-CLUSTER";

    /// <summary>The marker opening a planner dispatch that assesses an intake.</summary>
    public const string Intake = "INTAKE";

    /// <summary>The marker opening a specification phase dispatch.</summary>
    public const string Feature = "FEATURE";

    /// <summary>The marker naming the specification phase a dispatch runs.</summary>
    public const string Phase = "PHASE";

    /// <summary>The marker opening a dispatch that drains review findings.</summary>
    public const string Findings = "FINDINGS";

    /// <summary>The marker opening a plan's finalization dispatch.</summary>
    public const string Finalize = "FINALIZE";

    /// <summary>The closed set of header names as one alternation, for the line grammar.</summary>
    public const string NamesPattern =
        Marker + "|" + PlanFile + "|" + Task + "|" + Lens + "|" + Refute + "|" + ReviewRun + "|" + Technology + "|" + PriorResults + "|" + FailedAttempts;

    /// <summary>The closed set of header names, in the order the design lists them.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        Marker,
        PlanFile,
        Task,
        Lens,
        Refute,
        ReviewRun,
        Technology,
        PriorResults,
        FailedAttempts,
    ];

    /// <summary>The closed set of header names, as a set for membership checks.</summary>
    public static IReadOnlySet<string> ClosedNames { get; } =
        new HashSet<string>(Names, StringComparer.Ordinal);

    /// <summary>The routing markers that are not headers.</summary>
    public static IReadOnlyList<string> Markers { get; } =
    [
        RunStart,
        DocsImpact,
        FailureCluster,
        Intake,
        Feature,
        Phase,
        Findings,
        Finalize,
    ];
}
