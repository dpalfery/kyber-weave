using KyberWeave.Testing.Update;

namespace KyberWeave.Application.Tests.Update;

/// <summary>The self-update crash-safety contract, stopped at every operation of the in-memory file system.</summary>
public sealed class InMemorySelfUpdateCrashSafetyTests : SelfUpdateCrashSafetyContractTests
{
    /// <inheritdoc />
    protected override SelfUpdateCrashWorkspace CreateWorkspace() => new InMemorySelfUpdateCrashWorkspace();
}
