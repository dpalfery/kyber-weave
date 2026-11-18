using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Builds the real <see cref="SquadModelPresetSelectionStore"/> over the real file system and path semantics for the
/// tests whose subject is the files it leaves on disk. The class itself takes its seams, so every test that does not
/// need real files injects an in-memory file system instead.
/// </summary>
internal static class RealSquadSelectionStore
{
    /// <summary>The store over the physical file system, anchored on <paramref name="userPaths"/>.</summary>
    internal static SquadModelPresetSelectionStore Create(ISquadUserPaths userPaths) =>
        new(userPaths, new PhysicalFileSystem(), SquadFileSystemPathSemantics.Instance);
}
