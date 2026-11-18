using KyberWeave.Contracts.Host;
using KyberWeave.Tests.FileSystem;
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
    public void AKillInsideAJournalWriteLeavesATemporaryFileThatRecoveryDiscards()
    {
        for (int generation = 2; generation <= 6; generation++)
        {
            int intentTemporaryMoves = 0;
            int target = generation;
            Assert.Null(KillThenRecoverToTheOldTree(
                SquadCrashFlow.Install,
                operation => operation.Name == "MoveFile" && IsIntentTemporary(operation.Path) && ++intentTemporaryMoves == target));
        }
    }

    [Fact]
    public void AKillBeforeTheFirstJournalWriteLeavesOnlyEmptyFoldersThatRecoveryRemoves()
    {
        Assert.Null(KillThenRecoverToTheOldTree(
            SquadCrashFlow.Install,
            operation => operation.Name == "OpenWriteNew" && IsIntentTemporary(operation.Path)));
    }

    [Fact]
    public void AKillBetweenAStageMoveAndTheEmptyFolderRemovalLeavesFoldersRecoveryTolerates()
    {
        int stageMoves = 0;

        Assert.Null(KillThenRecoverToTheOldTree(
            SquadCrashFlow.Install,
            operation =>
            {
                if (stageMoves >= 2 && InMemorySquadCrashSafetyFixture.IsMutating(operation))
                {
                    return true;
                }

                stageMoves += operation.Name == "MoveFile"
                    && operation.Path!.Contains("staging", StringComparison.Ordinal)
                    && !operation.DestinationPath!.Contains("staging", StringComparison.Ordinal)
                    ? 1
                    : 0;
                return false;
            }));
    }

    [Fact]
    public void AKillDuringCleanupAfterTheCommitIsFinishedByRecoveryAtEveryStep()
    {
        foreach (SquadCrashFlow flow in new[] { SquadCrashFlow.Install, SquadCrashFlow.Update, SquadCrashFlow.Uninstall })
        {
            IReadOnlyDictionary<string, SquadTreeEntry> newTree;
            int deletesBeforeTheLastJournalWrite = 0;
            using (InMemorySquadCrashSafetyFixture clean = new())
            {
                clean.CreateBaseline(flow);
                int seen = 0;
                clean.Files.KillWhen(operation =>
                {
                    if (IsTransactionDelete(operation))
                    {
                        seen++;
                    }
                    else if (operation.Name == "MoveFile" && operation.DestinationPath!.EndsWith("intent.json", StringComparison.Ordinal))
                    {
                        deletesBeforeTheLastJournalWrite = seen;
                    }

                    return false;
                });
                clean.Transaction().Execute(clean.Plan(flow));
                clean.Files.ClearFaults();
                newTree = clean.Capture();
            }

            for (int step = 1; step < 200; step++)
            {
                using InMemorySquadCrashSafetyFixture fixture = new();
                fixture.CreateBaseline(flow);
                SquadDeploymentPlan plan = fixture.Plan(flow);
                int deletes = 0;
                int target = deletesBeforeTheLastJournalWrite + step;
                fixture.Files.KillWhen(operation => IsTransactionDelete(operation) && ++deletes == target);

                Exception? stopped = Record.Exception(() => fixture.Transaction().Execute(plan));
                if (stopped is null)
                {
                    Assert.True(step > 1, "Cleanup never deleted anything.");
                    break;
                }

                Assert.True(InMemorySquadCrashSafetyFixture.IsKill(stopped), "The flow failed without being killed.");
                fixture.Files.ClearFaults();
                fixture.Recover();

                Assert.Null(SquadTreeComparison.Difference(newTree, fixture.Capture()));
            }
        }
    }

    [Fact]
    public void AJournalWrittenBeforeTheCommittedPhaseStillRecoversToTheOldTree()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Update);
        IReadOnlyDictionary<string, SquadTreeEntry> oldTree = fixture.Capture();
        SquadDeploymentPlan plan = fixture.Plan(SquadCrashFlow.Update);
        fixture.Files.KillWhen(operation =>
            InMemorySquadCrashSafetyFixture.IsMutating(operation)
            && !operation.Path!.Contains(".kyber-weave", StringComparison.Ordinal)
            && !operation.Path.Contains(".squad-transaction", StringComparison.Ordinal));

        Exception? stopped = Record.Exception(() => fixture.Transaction().Execute(plan));
        Assert.True(stopped is not null && InMemorySquadCrashSafetyFixture.IsKill(stopped), "The flow was not killed.");
        fixture.Files.ClearFaults();
        string journal = fixture.Files.ReadAllText(
            fixture.Files.EnumerateFiles(fixture.Root, "intent.json", true).Single());
        Assert.Contains("\"prepared\"", journal, StringComparison.Ordinal);
        Assert.DoesNotContain("committed", journal, StringComparison.Ordinal);

        fixture.Recover();

        Assert.Null(SquadTreeComparison.Difference(oldTree, fixture.Capture()));
    }

    private static bool IsTransactionDelete(FileSystemOperation operation) =>
        operation.Name.StartsWith("Delete", StringComparison.Ordinal)
        && operation.Path!.Contains(".kyber-weave", StringComparison.Ordinal);

    private static bool IsIntentTemporary(string? path) =>
        path is not null && Path.GetFileName(path).StartsWith("intent.json.", StringComparison.Ordinal)
        && path.EndsWith(".tmp", StringComparison.Ordinal);

    private static string? KillThenRecoverToTheOldTree(SquadCrashFlow flow, Func<FileSystemOperation, bool> killWhen)
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(flow);
        IReadOnlyDictionary<string, SquadTreeEntry> oldTree = fixture.Capture();
        SquadDeploymentPlan plan = fixture.Plan(flow);

        fixture.Files.KillWhen(killWhen);
        Exception? stopped = Record.Exception(() => fixture.Transaction().Execute(plan));
        Assert.True(stopped is not null && InMemorySquadCrashSafetyFixture.IsKill(stopped), "The flow was not killed.");
        fixture.Files.ClearFaults();

        fixture.Recover();
        return SquadTreeComparison.Difference(oldTree, fixture.Capture());
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
