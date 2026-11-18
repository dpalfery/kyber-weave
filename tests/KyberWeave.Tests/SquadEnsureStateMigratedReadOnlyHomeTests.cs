using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The migration step that runs before every squad verb must not turn a read-only home into a failed verb.</summary>
public sealed class SquadEnsureStateMigratedReadOnlyHomeTests
{
    /// <summary>A home that refuses writes leaves a warning in the report, and the verb carries on.</summary>
    [Fact]
    public void AReadOnlyHomeIsAWarningAndNotAFailureOfTheVerb()
    {
        InMemorySquadState state = new();
        string root = state.CreateRoot("customer-alpha");
        state.Layout.WriteLegacyGlobal(root, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock());
        state.Migration.MigrateGlobal(invokingCwd: null);
        state.Files.DeleteFile(state.Layout.LedgerPath());
        state.Files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.WriteAllBytesAtomic),
            new UnauthorizedAccessException("read-only home"));

        SquadMigrationReport report = SquadTestCommands.InMemoryRoots(SquadTestCommands.CapturedSquadConsole(), files: state.Files).EnsureStateMigrated(
            state.Migration,
            invokingCwd: null,
            projectRoot: null,
            dryRun: false);

        Assert.Contains(report.Warnings, warning => warning.Contains("read-only home", StringComparison.Ordinal));
    }
}
