namespace KyberWeave.Cli.Update;

/// <summary>
/// Refreshes rendered Arbiter hooks after an update, in-process.
/// </summary>
/// <remarks>
/// The update replaces the <c>kyber-weave-arbiter</c> binary, whose hook source is embedded in the
/// CLI (<c>ArbiterPluginShim.Render</c>); already-deployed projects must be re-rendered or they keep
/// running the old hook source. The seam is constructor injection, never a child process:
/// implementations reuse <c>SquadLifecycleService.UpdateAsync</c> and never shell out to a hook script.
/// </remarks>
public interface IArbiterHookRefresher
{
    /// <summary>Re-renders Arbiter hooks for the given registered project roots.</summary>
    void Refresh(IReadOnlyList<string> projectRoots);
}
