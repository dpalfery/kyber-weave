using KyberWeave.Testing.Squad;

namespace KyberWeave.Tests.Squad;

/// <summary>The instance link sync crash-safety contract, stopped at every file operation of the in-memory file system.</summary>
public sealed class InMemorySquadInstanceLinkSyncCrashSafetyTests : SquadInstanceLinkSyncCrashSafetyContractTests
{
    /// <inheritdoc />
    protected override SquadLinkSyncCrashWorkspace CreateWorkspace() => new InMemorySquadLinkSyncCrashWorkspace();
}
