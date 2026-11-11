namespace KyberWeave.Core.Arbiter;

/// <summary>
/// The trigger family. The family fixes the effects its rules may use, what
/// undecidable maps to, and how answers combine. A host cannot remap
/// undecidable.
/// </summary>
public enum ArbiterTriggerFamily
{
    /// <summary><c>delegate</c> triggers: escalate wins, undecidable escalates.</summary>
    Conductor,

    /// <summary><c>investigate</c> triggers: escalate wins, undecidable escalates.</summary>
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
}

/// <summary>Family metadata: trigger mapping, effect sets, undecidable mapping and combination.</summary>
/// <remarks>
/// Answers are never averaged: any escalating rule escalates the trigger, and
/// likewise one confident red flag (<c>escalate</c>, <c>skip</c>, <c>verify</c>)
/// is enough to short-circuit step 1.
/// </remarks>
public static class ArbiterTriggerFamilies
{
    private static readonly Dictionary<string, ArbiterTriggerFamily> TriggerToFamily =
        new(StringComparer.Ordinal)
        {
            ["delegate"] = ArbiterTriggerFamily.Conductor,
            ["delegate.returned"] = ArbiterTriggerFamily.Conductor,
            ["delegate.planner"] = ArbiterTriggerFamily.Conductor,
            ["delegate.planner.returned"] = ArbiterTriggerFamily.Conductor,
            ["investigate"] = ArbiterTriggerFamily.Investigate,
            ["investigate.returned"] = ArbiterTriggerFamily.Investigate,
            ["lens.spawn"] = ArbiterTriggerFamily.LensSpawn,
            ["refute.spawn"] = ArbiterTriggerFamily.RefuteSpawn,
            ["lens.returned"] = ArbiterTriggerFamily.LensReturned,
            ["gate.select"] = ArbiterTriggerFamily.Gate,
            ["model.target"] = ArbiterTriggerFamily.ModelTarget,
            ["shortcut"] = ArbiterTriggerFamily.Shortcut,
            ["failure.cluster"] = ArbiterTriggerFamily.FailureCluster,
            ["advice"] = ArbiterTriggerFamily.Advice,
        };

    /// <summary>Every trigger name the Arbiter knows.</summary>
    public static IReadOnlyList<string> KnownTriggers => [.. TriggerToFamily.Keys];

    /// <summary>Resolves a trigger name to its family.</summary>
    /// <exception cref="ArgumentException">Thrown for an unknown trigger, with the known names as hint.</exception>
    public static ArbiterTriggerFamily ForTrigger(string trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (TriggerToFamily.TryGetValue(trigger, out ArbiterTriggerFamily family))
        {
            return family;
        }

        throw new ArgumentException(
            $"Unknown Arbiter trigger '{trigger}'. Known triggers: {string.Join(", ", TriggerToFamily.Keys)}.",
            nameof(trigger));
    }

    /// <summary>The effect set the family's rules may use.</summary>
    public static IReadOnlySet<string> AllowedEffects(ArbiterTriggerFamily family) => family switch
    {
        ArbiterTriggerFamily.Conductor => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Allow, Rules.RuleEffects.Escalate,
        },
        ArbiterTriggerFamily.Investigate => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Allow, Rules.RuleEffects.Escalate,
        },
        ArbiterTriggerFamily.LensSpawn => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Allow, Rules.RuleEffects.Skip,
        },
        ArbiterTriggerFamily.RefuteSpawn => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Allow, Rules.RuleEffects.Verify, Rules.RuleEffects.Drop, Rules.RuleEffects.Duplicate,
        },
        ArbiterTriggerFamily.LensReturned => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Allow, Rules.RuleEffects.Annotate, Rules.RuleEffects.Drop,
        },
        ArbiterTriggerFamily.Gate => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Applies, Rules.RuleEffects.NotApplicable,
        },
        ArbiterTriggerFamily.ModelTarget => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Keep, Rules.RuleEffects.Retarget,
        },
        ArbiterTriggerFamily.Shortcut => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Spawn, Rules.RuleEffects.Answer,
        },
        ArbiterTriggerFamily.FailureCluster => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Allow, Rules.RuleEffects.SameFailure,
        },
        ArbiterTriggerFamily.Advice => new HashSet<string>(StringComparer.Ordinal)
        {
            Rules.RuleEffects.Keep, Rules.RuleEffects.Advise,
        },
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    /// <summary>The fixed effect an undecidable answer maps to.</summary>
    public static string UndecidableEffect(ArbiterTriggerFamily family) => family switch
    {
        ArbiterTriggerFamily.Conductor => Rules.RuleEffects.Escalate,
        ArbiterTriggerFamily.Investigate => Rules.RuleEffects.Escalate,
        ArbiterTriggerFamily.LensSpawn => Rules.RuleEffects.Allow,
        ArbiterTriggerFamily.RefuteSpawn => Rules.RuleEffects.Allow,
        ArbiterTriggerFamily.LensReturned => Rules.RuleEffects.Allow,
        ArbiterTriggerFamily.Gate => Rules.RuleEffects.Applies,
        ArbiterTriggerFamily.ModelTarget => Rules.RuleEffects.Keep,
        ArbiterTriggerFamily.Shortcut => Rules.RuleEffects.Spawn,
        ArbiterTriggerFamily.FailureCluster => Rules.RuleEffects.Allow,
        ArbiterTriggerFamily.Advice => Rules.RuleEffects.Keep,
        _ => Rules.RuleEffects.Allow,
    };

    /// <summary>
    /// Combines per-rule effects: the red-flag effect wins
    /// (<c>escalate</c>, <c>skip</c>, <c>verify</c>); any <c>annotate</c> is
    /// delivered; any <c>applies</c> runs the gate.
    /// </summary>
    public static string Combine(ArbiterTriggerFamily family, IEnumerable<string> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        HashSet<string> seen = new(effects, StringComparer.Ordinal);
        return family switch
        {
            ArbiterTriggerFamily.Conductor or ArbiterTriggerFamily.Investigate =>
                seen.Contains(Rules.RuleEffects.Escalate) ? Rules.RuleEffects.Escalate : Rules.RuleEffects.Allow,
            ArbiterTriggerFamily.LensSpawn =>
                seen.Contains(Rules.RuleEffects.Skip) ? Rules.RuleEffects.Skip : Rules.RuleEffects.Allow,
            ArbiterTriggerFamily.RefuteSpawn =>
                seen.Contains(Rules.RuleEffects.Verify) ? Rules.RuleEffects.Verify
                : seen.Contains(Rules.RuleEffects.Drop) ? Rules.RuleEffects.Drop
                : seen.Contains(Rules.RuleEffects.Duplicate) ? Rules.RuleEffects.Duplicate
                : Rules.RuleEffects.Allow,
            ArbiterTriggerFamily.LensReturned =>
                seen.Contains(Rules.RuleEffects.Annotate) ? Rules.RuleEffects.Annotate
                : seen.Contains(Rules.RuleEffects.Drop) ? Rules.RuleEffects.Drop
                : Rules.RuleEffects.Allow,
            ArbiterTriggerFamily.Gate =>
                seen.Contains(Rules.RuleEffects.Applies) ? Rules.RuleEffects.Applies : Rules.RuleEffects.NotApplicable,
            ArbiterTriggerFamily.ModelTarget =>
                seen.Contains(Rules.RuleEffects.Retarget) ? Rules.RuleEffects.Retarget : Rules.RuleEffects.Keep,
            ArbiterTriggerFamily.Shortcut =>
                seen.Count == 1 && seen.Contains(Rules.RuleEffects.Answer) ? Rules.RuleEffects.Answer : Rules.RuleEffects.Spawn,
            ArbiterTriggerFamily.FailureCluster =>
                seen.Contains(Rules.RuleEffects.SameFailure) ? Rules.RuleEffects.SameFailure : Rules.RuleEffects.Allow,
            ArbiterTriggerFamily.Advice =>
                seen.Count == 1 && seen.Contains(Rules.RuleEffects.Advise) ? Rules.RuleEffects.Advise : Rules.RuleEffects.Keep,
            _ => Rules.RuleEffects.Allow,
        };
    }

    /// <summary>
    /// Whether the effect is the family's strongest — the step-1 short-circuit
    /// signal. One confident red flag is enough.
    /// </summary>
    public static bool IsStrongestEffect(string effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return string.Equals(effect, Rules.RuleEffects.Escalate, StringComparison.Ordinal)
            || string.Equals(effect, Rules.RuleEffects.Skip, StringComparison.Ordinal)
            || string.Equals(effect, Rules.RuleEffects.Verify, StringComparison.Ordinal);
    }
}
