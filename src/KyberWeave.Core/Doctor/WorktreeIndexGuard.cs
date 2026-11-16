using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Core.Doctor;

/// <summary>The worktree index guard's static entry point, for callers that have no container.</summary>
/// <remarks>
///     The logic is <c>WorktreeIndexEnsurer</c> in the application layer behind <see cref="IWorktreeIndexGuard"/>.
/// </remarks>
public static class WorktreeIndexGuard
{
    /// <inheritdoc cref="IWorktreeIndexGuard.Ensure"/>
    public static WorktreeIndexOutcome Ensure(
        string root,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory,
        CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles) =>
        DoctorServices.WorktreeIndex.Ensure(root, options, executor, lockDirectory, restore);
}
