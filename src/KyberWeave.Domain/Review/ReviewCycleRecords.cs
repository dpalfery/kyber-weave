using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>One finished review cycle, as its folder records it.</summary>
/// <param name="Number">The cycle number, from the folder name <c>cycle-&lt;n&gt;</c>.</param>
/// <param name="Folder">Absolute path of the cycle folder.</param>
/// <param name="Head">The commit the cycle reviewed, from its <c>head</c> file; null when the file is absent or not a commit id.</param>
/// <param name="Gates">The cycle's gate report, or null when it wrote none or it cannot be read.</param>
/// <param name="Branch">The branch the cycle reviewed, from <c>cycle.json</c>; null when none was recorded.</param>
/// <param name="Base">The merge-base commit of the change the cycle reviewed, from <c>cycle.json</c>; null when none was recorded.</param>
public sealed record ReviewCycleRecord(
    int Number, string Folder, string? Head, GateReport? Gates, string? Branch = null, string? Base = null)
{
    /// <summary>The file the verdict command writes into a cycle folder.</summary>
    public const string FindingsFileName = "findings.json";

    /// <summary>The findings document the verdict command wrote for this cycle.</summary>
    public string FindingsPath => Path.Combine(Folder, FindingsFileName);
}

/// <summary>The facts that tie a review cycle to one change: the branch, the commit the change forked from, and the commit reviewed.</summary>
/// <param name="Branch">The checked-out branch.</param>
/// <param name="Base">The merge-base commit of the base ref and <c>HEAD</c>.</param>
/// <param name="Head">The commit <c>HEAD</c> named.</param>
public sealed record ReviewCycleIdentity(string Branch, string Base, string Head);

/// <summary>A recorded cycle that does not belong to the current change, and the fact that says so.</summary>
/// <param name="Record">The cycle that was set aside.</param>
/// <param name="Reason">Why it does not belong, in one phrase.</param>
public sealed record IgnoredCycle(ReviewCycleRecord Record, string Reason);

/// <summary>The finished cycles that belong to the current change, and those that do not.</summary>
/// <param name="Valid">The cycles that count as prior, oldest first.</param>
/// <param name="Ignored">The cycles set aside, oldest first.</param>
public sealed record PriorCycles(IReadOnlyList<ReviewCycleRecord> Valid, IReadOnlyList<IgnoredCycle> Ignored);

/// <summary>What a cycle does about one declared gate.</summary>
public enum GateCycleAction
{
    /// <summary>The gate runs again: it failed, did not finish, or its inputs changed.</summary>
    Rerun,

    /// <summary>An earlier cycle's passing result still stands because no changed file is one of the gate's inputs.</summary>
    Carry,

    /// <summary>The gate does not apply: another operating system, or it has never applied to this change.</summary>
    NotApplicable
}

/// <summary>The decision for one gate, with the machine fact behind it.</summary>
/// <param name="GateId">The declared gate id.</param>
/// <param name="Action">What the cycle does with the gate.</param>
/// <param name="Reason">The fact the decision rests on, in one phrase.</param>
/// <param name="FromCycle">The cycle whose result is carried or was last seen, when there is one.</param>
/// <param name="Carried">The earlier result, when <see cref="Action"/> is <see cref="GateCycleAction.Carry"/>.</param>
public sealed record GateCycleDecision(
    string GateId,
    GateCycleAction Action,
    string Reason,
    int? FromCycle = null,
    GateResult? Carried = null);

/// <summary>Which cycle a review is in, and what the fix since the last cycle leaves to do.</summary>
/// <param name="Number">The cycle about to run: one more than the last finished cycle.</param>
/// <param name="Prior">The last finished cycle, or null when this is the first.</param>
/// <param name="Gates">What to do with each declared gate; empty in the first cycle.</param>
public sealed record ReviewCyclePlan(int Number, ReviewCycleRecord? Prior, IReadOnlyList<GateCycleDecision> Gates)
{
    /// <summary>Whether this cycle re-checks the prior findings instead of reviewing the change in full.</summary>
    public bool IsVerifier => Prior is not null;
}
