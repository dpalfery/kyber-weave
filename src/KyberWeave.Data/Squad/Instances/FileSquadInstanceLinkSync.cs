using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Links an instance's settings and history to the canonical harness home.</summary>
public sealed class FileSquadInstanceLinkSync : ISquadInstanceLinkSync
{
    /// <inheritdoc />
    public SquadInstanceLinkSyncResult Sync(
        string instanceRoot,
        string mainHome,
        SquadInstanceRow row,
        IReadOnlySet<string> renderedPaths,
        ISquadLinkFileSystem fileSystem,
        string instanceStateDirectory) =>
        SquadInstanceLinkSync.Sync(instanceRoot, mainHome, row, renderedPaths, fileSystem, instanceStateDirectory);
}
