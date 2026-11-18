using KyberWeave.Testing.Squad;
using KyberWeave.Tests;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The state-migration crash-safety contract on the real disk: the migration runs over the real file system, path
/// resolver and mutex lease, and the process is stopped before each file operation in turn.
/// </summary>
public sealed class PhysicalSquadStateMigrationCrashSafetyTests : SquadStateMigrationCrashSafetyContractTests, IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    protected override SquadStateCrashWorkspace CreateWorkspace() => new PhysicalSquadStateCrashWorkspace(_directory.Path);

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();
}
