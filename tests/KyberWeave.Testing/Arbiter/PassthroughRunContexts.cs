using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The run context a unit test gets by default: the payload's own directory, with no git, run-record or file read.
/// </summary>
/// <remarks>
/// A test whose subject is path resolution asks <c>HookRunContextResolver</c> directly, or injects a resolver that
/// answers what the test declares. Every other unit test only needs a context to exist, and resolving one for real
/// would start git and read the operator's home on every hook invocation.
/// </remarks>
internal sealed class PassthroughRunContexts : IHookRunContextResolver
{
    /// <inheritdoc />
    public HookRunContext Resolve(HookRunContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = request.WorkspaceRoot
            ?? request.EventCwd
            ?? request.ProcessDirectory
            ?? request.HomeDirectory;
        return new HookRunContext(
            root,
            CommonDirectory: null,
            ActiveRun: null,
            Worktree: root,
            Worktrees: [root],
            PlanFile: null,
            PlanPath: null,
            TaskFile: null,
            TaskFileExists: false,
            HookRunContextSource.PayloadCwd,
            Notes: []);
    }

    /// <inheritdoc />
    public HookRunContext SelectPlan(HookRunContext context, string? planFile, string? text) => context;

    /// <inheritdoc />
    public Task<HookRunContext> ResolveAsync(
        HookRunContextRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(request));

    /// <inheritdoc />
    public Task<HookRunContext> SelectPlanAsync(
        HookRunContext context,
        string? planFile,
        string? text,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(SelectPlan(context, planFile, text));
}
