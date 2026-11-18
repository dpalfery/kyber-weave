namespace KyberWeave.Core.Docs.Validation;

/// <summary>Chooses an agent for each task of a task file while the file is written.</summary>
public interface IPlanTaskAgentPicker : IDisposable
{
    /// <summary>
    /// The confident picks by task id. A task with no confident pick is left out, and a picker that cannot reach its
    /// model returns no picks at all rather than failing.
    /// </summary>
    /// <param name="tasks">The tasks that need an agent: the plan assigned them no role.</param>
    IReadOnlyDictionary<string, PlanTaskAgentChoice> Pick(IReadOnlyList<PlanTaskFileEntry> tasks);
}
