using KyberWeave.Testing.Squad;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The Squad deployment crash-safety contract, run against the real disk. The abstract suite lives
/// in <c>KyberWeave.Testing</c> so the same cases can run against the in-memory file system once the
/// deployment classes take that seam.
/// </summary>
public sealed class PhysicalSquadCrashSafetyTests : SquadCrashSafetyContractTests
{
    /// <inheritdoc />
    protected override SquadCrashSafetyFixture CreateFixture() => new PhysicalSquadCrashSafetyFixture();
}
