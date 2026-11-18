using KyberWeave.Contracts.Host;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// One test for each crash-safety weakness the 2026-11-17 review found in the deployment transaction before it moved
/// onto seams. Each reproduces the weakness on the in-memory file system and fails until the transaction is fixed.
/// </summary>
public sealed class SquadTransactionWeaknessTests
{
    [Fact]
    public void AnIoErrorWhileRollingBackKeepsTheJournalAndTheBackupsSoRecoveryFinishesTheJob()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Update);
        IReadOnlyDictionary<string, SquadTreeEntry> oldTree = fixture.Capture();
        SquadDeploymentPlan plan = fixture.Plan(SquadCrashFlow.Update);

        bool restoring = false;
        fixture.Files.FailWhen(
            operation =>
            {
                bool fails = restoring && operation.Name is "OpenWriteNew" or "MoveFile" or "DeleteFile";
                restoring &= !fails;
                return fails;
            },
            new IOException("The disk failed once while the transaction was rolling back."));
        FailOnFirstAppliedFile observer = new(() => restoring = true);

        _ = Assert.ThrowsAny<Exception>(() => fixture.Transaction(observer).Execute(plan));
        fixture.Files.ClearFaults();
        fixture.Recover();

        Assert.Null(SquadTreeComparison.Difference(oldTree, fixture.Capture()));
    }

    [Fact]
    public void TheProjectRegistryIsUpdatedUnderALeaseOfItsOwnSoTwoProjectsCannotLoseEachOthersEntry()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Install);
        SquadDeploymentPlan plan = fixture.Plan(SquadCrashFlow.Install);
        string registryLease = SquadLeaseNames.ForRegistry(fixture.StateStore.ResolveHomeStateDirectory());
        ILease other = new InProcessLease(fixture.LeaseHost);

        SquadTransaction transaction = fixture.Transaction();
        using (other.Acquire(registryLease, TimeSpan.Zero))
        {
            transaction.Execute(plan);
        }

        Assert.Contains(transaction.Warnings, warning => warning.Contains("registry was not updated", StringComparison.Ordinal));
    }

    private sealed class FailOnFirstAppliedFile(Action armRollbackFault) : ISquadTransactionObserver
    {
        public void AfterStep(SquadTransactionStep transactionStep)
        {
            if (transactionStep.Kind == SquadTransactionStepKind.FileApplied)
            {
                armRollbackFault();
                throw new IOException("The flow failed after the first file was applied.");
            }
        }
    }
}
