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

    /// <summary>
    /// The same answer, asked without blocking a thread: the tasks' questions go to the model at once and are awaited
    /// together. A picker that cannot reach its model returns no entries at all rather than failing; a cancellation
    /// stops the asks and is raised to the caller.
    /// </summary>
    /// <param name="tasks">The tasks that need an agent: the plan assigned them no role.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The answer per asked task id, with the same rules as <see cref="Pick"/>.</returns>
    Task<IReadOnlyDictionary<string, PlanTaskAgentChoice>> PickAsync(
        IReadOnlyList<PlanTaskFileEntry> tasks,
        CancellationToken cancellationToken = default);
}
