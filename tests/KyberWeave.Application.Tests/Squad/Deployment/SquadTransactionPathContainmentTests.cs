using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// A transaction resolves every path it captures and writes against an explicit root, never against the process
/// working directory, so running the same deployment from a different directory cannot move or delete anything
/// outside the transaction's own tree.
/// </summary>
public sealed class SquadTransactionPathContainmentTests
{
    [Fact]
    public void ARelativeDirectoryIsRefusedRatherThanResolvedAgainstTheProcessWorkingDirectory()
    {
        InMemorySquadState state = new();
        SquadTransaction transaction = state.Transaction();

        _ = Assert.Throws<ArgumentException>(() =>
            transaction.CaptureMissingDirectories("relative/directory", Path.Combine(state.Files.Root, "root")));
    }

    [Fact]
    public void ARelativeBoundaryIsRefusedRatherThanResolvedAgainstTheProcessWorkingDirectory()
    {
        InMemorySquadState state = new();
        SquadTransaction transaction = state.Transaction();

        _ = Assert.Throws<ArgumentException>(() =>
            transaction.CaptureMissingDirectories(Path.Combine(state.Files.Root, "root", "directory"), "relative/root"));
    }
}
