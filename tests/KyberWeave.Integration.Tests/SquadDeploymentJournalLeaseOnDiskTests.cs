using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The one deployment-journal case that needs the real disk: an active transaction holds its journal open with an
/// exclusive operating-system file share, which an in-memory file system cannot express. The lease and every other
/// transaction contract run in memory; the file-system contract suite proves the same exclusive open on the adapter.
/// </summary>
public sealed class SquadDeploymentJournalLeaseOnDiskTests : SquadDeploymentStateTestBase
{
    [Fact]
    public async Task ExecuteActiveTransactionHoldsItsJournalWithAnExclusiveOperatingSystemFileShare()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction transaction = Transaction(fixture.Path, blocker);
        SquadDeploymentPlan plan = fixture.CreateUpdatePlan();
        Task execution = BlockedTransaction.Start(() => transaction.Execute(plan));
        BlockedTransaction.WaitForStep(
            blocker.Reached,
            execution,
            "The transaction did not reach its durable intent checkpoint.");

        bool heldExclusiveLease;
        try
        {
            // The lease lives in the project journal beside the root, not in the home-anchored
            // state directory, so that is where an exclusive hold must be observable.
            string leaseDirectory = Store(fixture.Path).ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project);
            heldExclusiveLease = Directory.EnumerateFiles(
                    leaseDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Any(IsHeldWithExclusiveFileShare);
        }
        finally
        {
            blocker.Release.Set();
        }

        await execution;
        Assert.True(
            heldExclusiveLease,
            "An active transaction must own an atomically acquired exclusive lease, not rely on a check-then-create directory race.");
    }
}
