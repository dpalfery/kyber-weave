using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.RunStart;
using KyberWeave.Core.Squad.Briefs;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The note the hook attaches to the architect's <c>PLAN_READY</c> return in an autonomous run, so the conductor
/// finalizes the plan instead of asking the user to approve it. Decides only whether the note applies; the engine
/// attaches it.
/// </summary>
internal static class AutonomousPlanNudge
{
    /// <summary>The trigger of a planner dispatch's return.</summary>
    internal const string Trigger = "delegate.planner.returned";

    /// <summary>The note, word for word.</summary>
    internal const string Note =
        "AUTONOMOUS RUN: the plan is ready. Call docs_finalize_approved_plan_and_write_task_file now with the approval "
        + "'AUTONOMOUS_APPROVAL: plan-approve-execute (autonomous: recommendation accepted)'. Do not ask the user to "
        + "approve; then dispatch the ready tasks.";

    private const string PlanReady = "PLAN_READY";
    private const string Architect = "architect";

    /// <summary>Whether this return is the architect's <c>PLAN_READY</c> in a run recorded as autonomous.</summary>
    /// <param name="result">What the evaluation decided for the return.</param>
    /// <param name="target">The dispatch's target.</param>
    /// <param name="toolOutput">The return's text.</param>
    /// <param name="run">The active run record, or null when none is known.</param>
    internal static bool Applies(ArbiterEvaluationResult result, string? target, string? toolOutput, RunRecord? run) =>
        string.Equals(BriefAutonomy.Normalize(run?.Autonomy), "autonomous", StringComparison.Ordinal)
        && !result.IsError
        && result.Dispatches.Any(dispatch => string.Equals(dispatch.Trigger, Trigger, StringComparison.Ordinal))
        && IsArchitect(target)
        && string.Equals(
            ReturnMarkerParser.Parse(toolOutput, readAttestations: false)?.PlannerStatus,
            PlanReady,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an earlier ledger event already recorded this same return as <c>PLAN_READY</c>: same harness call id,
    /// or, when the harness gives none, the same dispatch, in the same harness, session and target. A return with no
    /// ledger event of its own has nothing to be scoped by and is never a repeat.
    /// </summary>
    /// <param name="events">The ledger.</param>
    /// <param name="current">The event this evaluation appended, or null when it is not on the ledger.</param>
    /// <param name="callId">The harness's call id for the return, or null.</param>
    internal static bool AlreadyNudged(IReadOnlyList<ArbiterLedgerEvent> events, ArbiterLedgerEvent? current, string? callId)
    {
        if (current is null)
        {
            return false;
        }

        string? key = string.IsNullOrWhiteSpace(callId) ? null : callId;
        string? preId = current.PreId;
        if (key is null && preId is null)
        {
            return false;
        }

        return events.Any(other =>
            !string.Equals(other.Id, current.Id, StringComparison.Ordinal)
            && string.Equals(other.Harness, current.Harness, StringComparison.Ordinal)
            && string.Equals(other.Session, current.Session, StringComparison.Ordinal)
            && string.Equals(other.Target, current.Target, StringComparison.Ordinal)
            && other.Phase == ArbiterLedgerPhases.Post
            && string.Equals(other.Trigger, Trigger, StringComparison.Ordinal)
            && string.Equals(other.Returns?.PlannerStatus, PlanReady, StringComparison.OrdinalIgnoreCase)
            && (key is not null
                ? string.Equals(other.CallId, key, StringComparison.Ordinal)
                : string.Equals(other.PreId, preId, StringComparison.Ordinal)));
    }

    private static bool IsArchitect(string? target)
    {
        string name = target?.Trim() ?? string.Empty;
        int separator = name.LastIndexOfAny([':', '/', '\\']);
        return string.Equals(separator < 0 ? name : name[(separator + 1)..], Architect, StringComparison.OrdinalIgnoreCase);
    }
}
