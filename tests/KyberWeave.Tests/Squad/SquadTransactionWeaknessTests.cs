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

    [Fact]
    public void AForeignFileThatAppearsAfterThePreconditionCheckIsRefusedAndLeftAlone()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Install);
        SquadDeploymentPlan plan = fixture.Plan(SquadCrashFlow.Install);
        string foreign = Path.Combine(fixture.Root, SquadCrashSafetyFixture.ConductorPath);
        Assert.False(fixture.Files.FileExists(foreign));

        // The callback runs under the lease after the preconditions passed and before the intent captures the files.
        Exception? refused = Record.Exception(() => fixture.Transaction().Execute(
            plan,
            _ => fixture.Files.WriteAllText(foreign, "a file someone else just wrote")));

        Assert.IsType<SquadDeploymentConflictException>(refused);
        Assert.Equal("a file someone else just wrote", fixture.Files.ReadAllText(foreign));
    }

    [Fact]
    public void EveryJournalRenameIsFollowedByAFlushOfItsFolder()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Install);
        List<FileSystemOperation> operations = [];
        fixture.Files.KillWhen(operation =>
        {
            operations.Add(operation);
            return false;
        });

        // The suites switch the device flush off for speed; this case is about the flush itself.
        using (SquadTransaction.UseSyncToDiskScope(true))
        {
            fixture.Transaction().Execute(fixture.Plan(SquadCrashFlow.Install));
        }

        int renames = 0;
        for (int index = 0; index < operations.Count; index++)
        {
            if (operations[index].Name != "MoveFile"
                || !operations[index].DestinationPath!.EndsWith("intent.json", StringComparison.Ordinal))
            {
                continue;
            }

            renames++;
            FileSystemOperation next = operations[index + 1];
            Assert.Equal("FlushDirectory", next.Name);
            Assert.Equal(Path.GetDirectoryName(operations[index].DestinationPath), next.Path);
        }

        Assert.True(renames > 0, "The journal was never renamed into place.");
    }

    [Fact]
    public void AKillAnywhereInAnUpdateOfASymbolicLinkedFileRecoversTheLinkOrTheNewFile()
    {
        SquadCrashFlow flow = SquadCrashFlow.Update;
        int mutating = 0;
        IReadOnlyDictionary<string, SquadTreeEntry> oldTree;
        IReadOnlyDictionary<string, SquadTreeEntry> newTree;
        using (InMemorySquadCrashSafetyFixture clean = new())
        {
            LinkTheConductor(clean);
            oldTree = clean.Capture();
            clean.Files.KillWhen(operation =>
            {
                mutating += InMemorySquadCrashSafetyFixture.IsMutating(operation) ? 1 : 0;
                return false;
            });
            clean.Transaction().Execute(clean.Plan(flow));
            clean.Files.ClearFaults();
            newTree = clean.Capture();
        }

        List<string> failures = [];
        for (int point = 1; point <= mutating; point++)
        {
            using InMemorySquadCrashSafetyFixture fixture = new();
            LinkTheConductor(fixture);
            SquadDeploymentPlan plan = fixture.Plan(flow);
            int seen = 0;
            int target = point;
            fixture.Files.KillWhen(operation => InMemorySquadCrashSafetyFixture.IsMutating(operation) && ++seen == target);
            _ = Record.Exception(() => fixture.Transaction().Execute(plan));
            fixture.Files.ClearFaults();
            fixture.Recover();

            IReadOnlyDictionary<string, SquadTreeEntry> recovered = fixture.Capture();
            if (SquadTreeComparison.Difference(oldTree, recovered) is { } old
                && SquadTreeComparison.Difference(newTree, recovered) is { } fresh)
            {
                failures.Add($"op:{point}: {old} / {fresh}");
            }
        }

        Assert.Empty(failures);
    }

    private static void LinkTheConductor(InMemorySquadCrashSafetyFixture fixture)
    {
        fixture.CreateBaseline(SquadCrashFlow.Update);
        string conductor = Path.Combine(fixture.Root, SquadCrashSafetyFixture.ConductorPath);
        string real = Path.Combine(fixture.Root, "notes", "conductor-real.toml");
        fixture.Files.MoveFile(conductor, real);
        fixture.Files.CreateFileLink(conductor, real);
    }

    [Fact]
    public void ARealDiskFaultWrapperRefusesEveryOperationAfterTheKill()
    {
        FaultInjectingFileSystem files = new(new InMemoryFileSystem());
        files.KillAt(2);
        _ = files.FileExists("/a");

        _ = Assert.Throws<SquadCrashInjectedException>(() => files.FileExists("/a"));
        _ = Assert.Throws<SquadCrashInjectedException>(() => files.FileExists("/a"));

        files.Restart();
        Assert.False(files.FileExists("/a"));
    }

    [Fact]
    public void AKillDuringCleanupAfterAnInlineRollbackIsFinishedByRecoveryAtEveryStep()
    {
        foreach (SquadCrashFlow flow in new[] { SquadCrashFlow.Install, SquadCrashFlow.Update, SquadCrashFlow.Uninstall })
        {
            IReadOnlyDictionary<string, SquadTreeEntry> oldTree;
            using (InMemorySquadCrashSafetyFixture baseline = new())
            {
                baseline.CreateBaseline(flow);
                oldTree = baseline.Capture();
            }

            for (int step = 1; step < 200; step++)
            {
                using InMemorySquadCrashSafetyFixture fixture = new();
                fixture.CreateBaseline(flow);
                SquadDeploymentPlan plan = fixture.Plan(flow);
                bool failed = false;
                int deletes = 0;
                int target = step;

                // One failure while the files are applied sends the transaction down its inline rollback; the kill
                // then lands on the nth delete of the cleanup that follows it.
                fixture.Files.FailWhen(
                    operation =>
                    {
                        bool fails = !failed && operation.Name == "MoveFile"
                            && (operation.DestinationPath!.Contains("claimed-", StringComparison.Ordinal)
                                || (operation.Path!.Contains("staging", StringComparison.Ordinal)
                                    && !operation.DestinationPath.Contains(".kyber-weave", StringComparison.Ordinal)));
                        failed |= fails;
                        return fails;
                    },
                    new IOException("The disk failed once while the files were applied."));
                fixture.Files.KillWhen(operation =>
                {
                    return failed && IsTransactionDelete(operation) && ++deletes == target;
                });

                Exception? stopped = Record.Exception(() => fixture.Transaction().Execute(plan));
                Assert.NotNull(stopped);
                if (!InMemorySquadCrashSafetyFixture.IsKill(stopped))
                {
                    Assert.True(step > 1 || !failed, "The rollback never reached its cleanup.");
                    break;
                }

                fixture.Files.ClearFaults();
                fixture.Recover();

                Assert.Null(SquadTreeComparison.Difference(oldTree, fixture.Capture()));
            }
        }
    }

    [Fact]
    public void AFileThatAppearsInTheToolFolderWhileTheJournalIsRemovedSurvives()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Install);
        string userFile = Path.Combine(fixture.Root, ".kyber-weave", "kyber-weave.yml");
        bool written = false;

        // Another tool (docs init, onboarding) writes into the folder after the transaction decided it was empty.
        fixture.Files.KillWhen(operation =>
        {
            bool removingTheJournal = operation.Path is not null
                && ((operation.Name == "DeleteFile" && operation.Path.EndsWith("intent.json", StringComparison.Ordinal))
                    || (operation.Name == "DeleteTreeNoFollow" && operation.Path.EndsWith(".kyber-weave", StringComparison.Ordinal)));
            if (removingTheJournal && !written)
            {
                written = true;
                fixture.Files.WriteAllText(userFile, "user content");
            }

            return false;
        });

        fixture.Transaction().Execute(fixture.Plan(SquadCrashFlow.Install));

        Assert.True(written, "The journal was never removed.");
        Assert.Equal("user content", fixture.Files.ReadAllText(userFile));
    }

    [Fact]
    public void AJournalWithAnUnknownPhaseIsRefusedAndTheTreeLeftUntouched()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Update);
        SquadDeploymentPlan plan = fixture.Plan(SquadCrashFlow.Update);
        fixture.Files.KillWhen(operation =>
            InMemorySquadCrashSafetyFixture.IsMutating(operation)
            && !operation.Path!.Contains(".kyber-weave", StringComparison.Ordinal));
        Exception? stopped = Record.Exception(() => fixture.Transaction().Execute(plan));
        Assert.True(stopped is not null && InMemorySquadCrashSafetyFixture.IsKill(stopped), "The flow was not killed.");
        fixture.Files.ClearFaults();
        string journal = fixture.Files.EnumerateFiles(fixture.Root, "intent.json", true).Single();
        string text = fixture.Files.ReadAllText(journal);
        Assert.Contains("\"phase\": \"prepared\"", text, StringComparison.Ordinal);

        // A journal a newer build wrote with a phase this build does not know must not be guessed at.
        fixture.Files.WriteAllText(journal, text.Replace("\"phase\": \"prepared\"", "\"phase\": \"sealed\"", StringComparison.Ordinal));
        IReadOnlyDictionary<string, SquadTreeEntry> before = fixture.Capture();

        _ = Assert.Throws<InvalidDataException>(() => fixture.Recover());

        Assert.Null(SquadTreeComparison.Difference(before, fixture.Capture()));
    }

    [Fact]
    public void AnOriginalThatIsARelativeDirectoryLinkIsRestoredWithItsRelativeTarget()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Update);
        SquadDeploymentPlan plan = fixture.Plan(SquadCrashFlow.Update);
        string conductor = Path.Combine(fixture.Root, SquadCrashSafetyFixture.ConductorPath);
        string destination = Path.Combine(fixture.Root, "linked-directory");

        // After the preconditions passed and the journal was written, something swaps the owned file for a link to a
        // directory, so the claim meets a node it did not expect and the transaction must put it back as it was.
        LinkSwap swap = new(() =>
        {
            fixture.Files.DeleteFile(conductor);
            fixture.Files.CreateDirectory(destination);
            fixture.Files.CreateDirectoryLink(conductor, Path.Combine("..", "..", "linked-directory"));
        });

        _ = Assert.ThrowsAny<Exception>(() => fixture.Transaction(swap).Execute(plan));

        Assert.Equal(Path.Combine("..", "..", "linked-directory"), fixture.Files.GetLinkTarget(conductor));
        Assert.True(fixture.Files.DirectoryExists(destination));
    }

    private sealed class LinkSwap(Action swap) : ISquadTransactionObserver
    {
        private bool _done;

        public void AfterStep(SquadTransactionStep transactionStep)
        {
            if (!_done && transactionStep.Kind == SquadTransactionStepKind.IntentWritten)
            {
                _done = true;
                swap();
            }
        }
    }

    [Fact]
    public void AGlobalJournalFolderWithNoJournalIsRemovedButNothingAboveTheToolFolder()
    {
        InMemorySquadState state = new();
        string target = state.CreateRoot("target");
        string journalFolder = state.Store.ResolveTransactionDirectory(target, SquadDeploymentScope.Global);
        state.Files.CreateDirectory(journalFolder);
        string home = state.Layout.PhysicalOf(state.Layout.Home);
        Assert.True(state.Files.DirectoryExists(home));

        state.Transaction().Recover(target, SquadDeploymentScope.Global);

        Assert.False(state.Files.DirectoryExists(journalFolder));
        Assert.True(state.Files.DirectoryExists(home), "The pre-existing home folder was removed.");
    }

    [Fact]
    public void AFileThatAppearsWhileTheJournalFolderIsBeingRemovedDoesNotFailACommittedDeployment()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Install);
        string toolFolder = Path.Combine(fixture.Root, ".kyber-weave");
        string userFile = Path.Combine(toolFolder, "kyber-weave.yml");
        bool written = false;

        // The file lands after the folder was found empty and before it is deleted: the delete itself is refused.
        fixture.Files.KillWhen(operation =>
        {
            if (!written && operation.Name == "DeleteEmptyDirectory" && operation.Path!.EndsWith(".kyber-weave", StringComparison.Ordinal))
            {
                written = true;
                fixture.Files.WriteAllText(userFile, "user content");
            }

            return false;
        });

        fixture.Transaction().Execute(fixture.Plan(SquadCrashFlow.Install));

        Assert.True(written, "The tool folder was never removed.");
        Assert.Equal("user content", fixture.Files.ReadAllText(userFile));
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
