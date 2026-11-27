namespace KyberWeave.Core.Arbiter;

/// <summary>The one spelling of every trigger name the Arbiter knows.</summary>
public static class ArbiterTriggers
{
    /// <summary>A conductor dispatch that executes work.</summary>
    public const string Delegate = "delegate";

    /// <summary>The return of a <see cref="Delegate"/> dispatch.</summary>
    public const string DelegateReturned = "delegate.returned";

    /// <summary>A conductor dispatch to a planner.</summary>
    public const string DelegatePlanner = "delegate.planner";

    /// <summary>The return of a <see cref="DelegatePlanner"/> dispatch.</summary>
    public const string DelegatePlannerReturned = "delegate.planner.returned";

    /// <summary>A read-only investigation dispatch.</summary>
    public const string Investigate = "investigate";

    /// <summary>The return of an <see cref="Investigate"/> dispatch.</summary>
    public const string InvestigateReturned = "investigate.returned";

    /// <summary>A review lens seat's dispatch.</summary>
    public const string LensSpawn = "lens.spawn";

    /// <summary>A refutation seat's dispatch.</summary>
    public const string RefuteSpawn = "refute.spawn";

    /// <summary>A lens seat's return.</summary>
    public const string LensReturned = "lens.returned";

    /// <summary>A refutation seat's return.</summary>
    public const string RefuteReturned = "refute.returned";

    /// <summary>A gate selection.</summary>
    public const string GateSelect = "gate.select";

    /// <summary>The model-target family's alias trigger.</summary>
    public const string ModelTarget = "model.target";

    /// <summary>A model target chosen for a delegation.</summary>
    public const string DelegateModel = "delegate.model";

    /// <summary>The shortcut family's alias trigger.</summary>
    public const string Shortcut = "shortcut";

    /// <summary>An intake shortcut.</summary>
    public const string IntakeShortcut = "intake.shortcut";

    /// <summary>A bug-crusher triage shortcut.</summary>
    public const string TriageShortcut = "triage.shortcut";

    /// <summary>A documentation-impact shortcut.</summary>
    public const string DocsImpactShortcut = "docs-impact.shortcut";

    /// <summary>The failure-cluster family's alias trigger.</summary>
    public const string FailureCluster = "failure.cluster";

    /// <summary>A rework of a failure cluster.</summary>
    public const string DelegateFailure = "delegate.failure";

    /// <summary>The advice family's alias trigger.</summary>
    public const string Advice = "advice";

    /// <summary>A section selection.</summary>
    public const string SectionsSelect = "sections.select";

    /// <summary>A bot review comment's triage.</summary>
    public const string CommentTriage = "comment.triage";

    /// <summary>A review verdict's finding judgement.</summary>
    public const string ReviewVerdict = "review.verdict";

    /// <summary>A read-only worker's own command call.</summary>
    public const string WorkerCommand = "worker.command";

    /// <summary>Every trigger name the Arbiter knows, in the family registry's order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Delegate,
        DelegateReturned,
        DelegatePlanner,
        DelegatePlannerReturned,
        Investigate,
        InvestigateReturned,
        LensSpawn,
        RefuteSpawn,
        LensReturned,
        RefuteReturned,
        GateSelect,
        ModelTarget,
        DelegateModel,
        Shortcut,
        IntakeShortcut,
        TriageShortcut,
        DocsImpactShortcut,
        FailureCluster,
        DelegateFailure,
        Advice,
        SectionsSelect,
        CommentTriage,
        ReviewVerdict,
        WorkerCommand,
    ];
}
