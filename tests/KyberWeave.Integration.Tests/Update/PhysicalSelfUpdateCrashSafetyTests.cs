using KyberWeave.Testing.Update;
using KyberWeave.Tests;

namespace KyberWeave.Integration.Tests.Update;

/// <summary>The self-update crash-safety contract on the real disk: the process is killed before each file operation in turn.</summary>
public sealed class PhysicalSelfUpdateCrashSafetyTests : SelfUpdateCrashSafetyContractTests, IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    protected override SelfUpdateCrashWorkspace CreateWorkspace() => new PhysicalSelfUpdateCrashWorkspace(_directory.Path);

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();
}
