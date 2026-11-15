using KyberWeave.Domain.Hooks;

namespace KyberWeave.Contracts.Services;

/// <summary>Checks a file an agent just edited and reports what it should fix.</summary>
public interface IEditHookService
{
    /// <summary>Runs the matching lint and test rules for an edit.</summary>
    /// <param name="edit">The edit to check.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Exit code 2 with feedback when a blocking check fails; otherwise exit code 0. Never throws on internal errors.</returns>
    Task<HookResult> RunAsync(HookEditEvent edit, CancellationToken cancellationToken);
}
