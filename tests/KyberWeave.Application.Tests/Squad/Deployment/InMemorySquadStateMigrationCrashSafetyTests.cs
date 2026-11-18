using KyberWeave.Testing.Squad;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>The state-migration crash-safety contract, stopped at every file operation of the in-memory file system.</summary>
public sealed class InMemorySquadStateMigrationCrashSafetyTests : SquadStateMigrationCrashSafetyContractTests
{
    /// <inheritdoc />
    protected override SquadStateCrashWorkspace CreateWorkspace() => new InMemorySquadStateCrashWorkspace();
}
