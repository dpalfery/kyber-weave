using KyberWeave.Core.Arbiter.Plans;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>One task of the active plan, with its Test contract row, as the packet and the task gate both read it.</summary>
/// <param name="Plan">The plan, repository-relative.</param>
/// <param name="Entry">The task's entry in the plan's task file.</param>
/// <param name="Row">The task's Test contract row, or null when the plan has none for it.</param>
public sealed record PlanTaskTargetMatch(string Plan, PlanTaskFileEntry Entry, PlanContractRow? Row);
