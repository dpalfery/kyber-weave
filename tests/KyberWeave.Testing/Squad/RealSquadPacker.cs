using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Testing.Squad;

/// <summary>A Squad packer over the real file system, for a test that needs a tree on disk.</summary>
public static class RealSquadPacker
{
    /// <summary>Creates a packer that reads and writes the real disk.</summary>
    /// <returns>The packer.</returns>
    public static SquadPacker Create() => new(new PhysicalFileSystem(), new FileSquadCompanionSkills());
}
