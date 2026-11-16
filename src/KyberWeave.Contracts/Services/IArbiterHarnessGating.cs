using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Works out how each installed harness is gated for one project, for <c>arbiter doctor</c> and <c>arbiter status</c>.
/// A harness is installed when the global targets list it, the global Squad receipt owns an Arbiter hook for it, or a
/// project deployment reaches it. Nothing is written.
/// </summary>
public interface IArbiterHarnessGating
{
    /// <summary>Builds the rows for every harness installed for <paramref name="root"/>.</summary>
    /// <param name="root">The project root whose project hooks and deployments are read.</param>
    /// <param name="home">The home directory whose global targets, receipt and user hooks are read.</param>
    /// <param name="config">The project configuration; an unset <c>arbiter.enabled</c> reads as not configured.</param>
    /// <param name="globalTargets">The harnesses listed under <c>arbiter.global.targets</c>.</param>
    /// <returns>The rows and the unlisted global harnesses.</returns>
    HarnessGatingReport Build(
        string root,
        string home,
        KyberWeaveConfig config,
        IReadOnlyList<string> globalTargets);
}
