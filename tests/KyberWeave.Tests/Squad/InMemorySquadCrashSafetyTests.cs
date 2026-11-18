using KyberWeave.Testing.Squad;

namespace KyberWeave.Tests;

/// <summary>
/// The Squad deployment crash-safety contract, run against the in-memory file system: a process killed at every
/// mutating file-system operation of install, update and uninstall, then recovered, and every handled failure rolled
/// back. The same cases run on the real disk in the integration project.
/// </summary>
public sealed class InMemorySquadCrashSafetyTests : SquadCrashSafetyContractTests
{
    /// <summary>Every stop point now recovers to the old or the complete new tree, so nothing is known to fail.</summary>
    protected override IReadOnlyList<SquadCrashKnownWeakness> KnownWeaknesses => [];

    /// <inheritdoc />
    protected override SquadCrashSafetyFixture CreateFixture() => new InMemorySquadCrashSafetyFixture();
}
