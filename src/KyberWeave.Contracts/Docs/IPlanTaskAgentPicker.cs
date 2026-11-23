namespace KyberWeave.Core.Docs.Validation;

/// <summary>Chooses an agent for each task of a task file while the file is written.</summary>
public interface IPlanTaskAgentPicker : IDisposable
{
    /// <summary>
    /// The answer per asked task id. A task with no confident pick still gets an entry, with a null
    /// <see cref="PlanTaskAgentChoice.Agent"/>, because its difficulty check does not depend on the pick; a picker
    /// that cannot reach its model returns no entries at all rather than failing.
    /// </summary>
    /// <param name="tasks">The tasks that need an agent: the plan assigned them no role.</param>
    IReadOnlyDictionary<string, PlanTaskAgentChoice> Pick(IReadOnlyList<PlanTaskFileEntry> tasks);
}
