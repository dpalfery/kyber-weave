using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Links a harness instance's settings and history to the canonical harness home.</summary>
public interface ISquadInstanceLinkSync
{
    /// <summary>Synchronizes the links of one instance.</summary>
    SquadInstanceLinkSyncResult Sync(
        string instanceRoot,
        string mainHome,
        SquadInstanceRow row,
        IReadOnlySet<string> renderedPaths,
        ISquadLinkFileSystem fileSystem,
        string instanceStateDirectory);
}
