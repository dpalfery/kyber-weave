namespace KyberWeave.Core.Arbiter;

/// <summary>
/// The trigger family. The family fixes the effects its rules may use, what
/// undecidable maps to, and how answers combine. A host cannot remap
/// undecidable, and no family maps it to a block: the Arbiter blocks only on a
/// positive finding, never because it could not decide (ADR 0072).
/// </summary>
public enum ArbiterTriggerFamily
{
    /// <summary><c>delegate</c> triggers: escalate wins, undecidable allows.</summary>
    Conductor,

    /// <summary><c>investigate</c> triggers: escalate wins, undecidable allows.</summary>
    Investigate,

    /// <summary><c>lens.spawn</c>: skip wins, undecidable allows.</summary>
    LensSpawn,

    /// <summary><c>refute.spawn</c>: verify wins, undecidable allows.</summary>
    RefuteSpawn,

    /// <summary><c>lens.returned</c>: every annotate is delivered, undecidable allows.</summary>
    LensReturned,

    /// <summary><c>gate.select</c>: undecidable runs the gate.</summary>
    Gate,

    /// <summary><c>model.target</c>: any retarget wins, undecidable keeps the current model.</summary>
    ModelTarget,

    /// <summary><c>shortcut</c>: answer only when every rule agrees, undecidable spawns.</summary>
    Shortcut,

    /// <summary><c>failure.cluster</c>: any same-failure wins, undecidable allows.</summary>
    FailureCluster,

    /// <summary><c>advice</c>: advise only when every rule agrees, undecidable keeps.</summary>
    Advice,

    /// <summary><c>worker.command</c>: escalate wins, undecidable allows.</summary>
    WorkerCommand,
}
