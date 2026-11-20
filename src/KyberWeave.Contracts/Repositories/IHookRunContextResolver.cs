using KyberWeave.Arbiter.Hooks;

namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// Resolves the one run context a hook invocation reads its paths from: the repository, the active run and its
/// worktree, and the dispatch's plan and task file.
/// </summary>
/// <remarks>
/// Every guard, rule and prompt builder reads paths from the context this returns, so the precedence between the
/// worktree an event names, the run record and the payload's <c>cwd</c> is decided in one place. Each candidate is
/// accepted only when it belongs to this repository or one of its own git worktrees; a candidate that does not is
/// refused with its reason and never silently trusted.
/// </remarks>
public interface IHookRunContextResolver
{
    /// <summary>
    /// Resolves the context for one hook invocation. A plan, run record or task file that cannot be read leaves the
    /// corresponding member null or false and adds a note naming the reason and the path tried; it does not throw.
    /// </summary>
    /// <param name="request">What the typed hook event and the process report.</param>
    HookRunContext Resolve(HookRunContextRequest request);

    /// <summary>
    /// Re-reads the plan and task file of an already-validated context for a text whose plan differs from the
    /// event's — a return whose plan was resolved from the worktree its body names, for example. No git or run record
    /// is read: only the context's validated worktrees are consulted.
    /// </summary>
    /// <param name="context">The context the invocation already resolved.</param>
    /// <param name="planFile">The plan the text names, or null for none.</param>
    /// <param name="text">The text to read a named worktree from, when it names no plan.</param>
    HookRunContext SelectPlan(HookRunContext context, string? planFile, string? text);
}
