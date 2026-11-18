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

/// <summary>
/// The same contract with the sync given the product's own link file system, so the symbolic links the sync creates on
/// Unix and macOS (and the junctions and links on Windows) are the shipped ones, not the test adapter's.
/// </summary>
public sealed class ShippedLinksSquadInstanceLinkSyncCrashSafetyTests : SquadInstanceLinkSyncCrashSafetyContractTests, IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    protected override SquadLinkSyncCrashWorkspace CreateWorkspace() =>
        new PhysicalSquadLinkSyncCrashWorkspace(_directory.Path, shippedLinks: true);

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();
}
