using KyberWeave.Testing.Squad;
using KyberWeave.Tests;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The instance link sync crash-safety contract on the real disk: the sync runs over the real file system, path resolver
/// and mutex lease, and the process is stopped before each file operation in turn.
/// </summary>
public sealed class PhysicalSquadInstanceLinkSyncCrashSafetyTests : SquadInstanceLinkSyncCrashSafetyContractTests, IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    protected override SquadLinkSyncCrashWorkspace CreateWorkspace() => new PhysicalSquadLinkSyncCrashWorkspace(_directory.Path);

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();
}
