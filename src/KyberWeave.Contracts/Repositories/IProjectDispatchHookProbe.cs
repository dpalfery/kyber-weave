namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// Finds whether a project carries its own project-scope Arbiter dispatch hook for one harness.
/// </summary>
/// <remarks>
/// The user-level hook asks this before it defers a dispatch to the project. A project with no hook of its
/// own leaves the dispatch with no gate and no inserted task prompt, so the user-level hook answers that
/// dispatch itself rather than deferring to a hook that will never fire. Five harnesses are read:
/// <c>antigravity</c>, <c>factory</c>, <c>cursor</c>, <c>opencode</c> and <c>pi</c>.
/// </remarks>
public interface IProjectDispatchHookProbe
{
    /// <summary>
    /// Whether the project carries a project-scope Arbiter dispatch hook for <paramref name="harnessToken"/>.
    /// </summary>
    /// <param name="projectRoot">The repository root the project's own hook files live under.</param>
    /// <param name="harnessToken">The harness token the hook serves, such as <c>antigravity</c>, <c>factory</c>,
    /// <c>cursor</c>, <c>opencode</c> or <c>pi</c>.</param>
    /// <returns>
    /// True when the project's own hook covers the harness, so the user-level hook defers to it. A harness this
    /// probe does not read returns true: those harnesses keep their existing deferral.
    /// </returns>
    bool CoversHarness(string projectRoot, string harnessToken);
}
