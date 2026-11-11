namespace KyberWeave.Cli.Update;

/// <summary>The outcome of one arbiter hook refresh over the registered projects.</summary>
/// <param name="Refreshed">Projects whose hooks were re-rendered.</param>
/// <param name="Failed">Projects whose refresh was attempted and failed.</param>
public readonly record struct ArbiterHookRefreshResult(int Refreshed, int Failed);

/// <summary>
/// Refreshes rendered Arbiter hooks after an update, in-process.
/// </summary>
/// <remarks>
/// The update replaces the <c>kyber-weave-arbiter</c> binary, whose hook source is embedded in the
/// CLI (<c>ArbiterPluginShim.Render</c>); already-deployed projects must be re-rendered or they keep
/// running the old hook source. The refresh runs inside the new kyber-weave, which the old process starts
/// after the swap, so it renders from that release's own templates. It never runs a hook script or the
/// arbiter binary: implementations reuse <c>SquadLifecycleService.UpdateAsync</c> in-process.
/// </remarks>
public interface IArbiterHookRefresher
{
    /// <summary>
    /// Re-renders Arbiter hooks for the given registered project roots and reports how many
    /// projects were refreshed and how many failed, so the caller can report the real outcome
    /// rather than the number of inputs.
    /// </summary>
    ArbiterHookRefreshResult Refresh(IReadOnlyList<string> projectRoots);
}
