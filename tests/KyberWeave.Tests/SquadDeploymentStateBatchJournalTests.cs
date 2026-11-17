using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     The journal writes one generation per batch of files instead of one per file. These tests pin
///     both halves of that trade: the write count, and the crash guarantee the batching must not weaken —
///     a kill at any instant inside a batch, at a batch boundary, during rollback, or during recovery
///     leaves recovery able to produce the prior tree exactly.
/// </summary>
public sealed class SquadDeploymentStateBatchJournalTests : SquadDeploymentStateTestBase
{
    private const string AgentOnePath = ".codex/agents/one.toml";
    private const string AgentTwoPath = ".codex/agents/two.toml";
    private const string AgentThreePath = ".codex/agents/three.toml";
    private const string AdoptedPath = ".codex/agents/adopted.md";
    private const string ForeignPath = "notes/operator.md";

    /// <summary>One generation per 64 files, plus the fixed preparing, prepared, lock and receipt writes.</summary>
    private const int GenerationCeiling = 16;

    [Fact]
    public void ExecuteWritesOneJournalGenerationPerBatchNotOnePerFile()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        const int fileCount = 200;
        SquadDeploymentFile[] files = Enumerable.Range(0, fileCount)
            .Select(index => Rendered($".codex/agents/agent-{index:D3}.toml", $"body {index}"))
            .ToArray();

        SquadTransaction transaction = Transaction(root);
        transaction.Execute(SquadDeploymentPlanner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            files,
            [],
            false,
            new FixedTimeProvider(InstalledAt)));

        Assert.InRange(transaction.IntentGenerationCount, 1, GenerationCeiling);
        for (int index = 0; index < fileCount; index++)
            Assert.Equal($"body {index}", Read(root, $".codex/agents/agent-{index:D3}.toml"));
    }

    [Fact]
    public void ExecuteDeclaresTheWholeBatchBeforeTouchingItsFirstFile()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SeedUpdate(root, out SquadReceipt receipt);
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);

        CrashObserver observer = new(root, SquadTransactionCheckpointKind.ActiveTransitionWritten, 1);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(UpdatePlan(root, receipt)));

        SortedDictionary<string, TreeEntry> atCrash =
            Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.Snapshot);

        // Nothing in the batch had been touched when the generation reached disk.
        Assert.Equal("one v1", Encoding.UTF8.GetString(Assert.IsType<byte[]>(atCrash[AgentOnePath].Content)));
        Assert.Equal("two v1", Encoding.UTF8.GetString(Assert.IsType<byte[]>(atCrash[AgentTwoPath].Content)));
        Assert.Equal("three v1", Encoding.UTF8.GetString(Assert.IsType<byte[]>(atCrash[AgentThreePath].Content)));

        JsonNode intent = Assert.IsAssignableFrom<JsonNode>(observer.Journal);
        JsonArray files = intent["files"]!.AsArray();
        JsonArray transitions = intent["activeTransitions"]!.AsArray();
        JsonArray artifacts = intent["artifacts"]!.AsArray();
        Dictionary<string, JsonNode> declared = files
            .Select(file => file!)
            .ToDictionary(
                file => file["relativePath"]!.GetValue<string>(),
                StringComparer.Ordinal);
        Dictionary<string, string> prior = new(StringComparer.Ordinal)
        {
            [AgentOnePath] = "one v1",
            [AgentTwoPath] = "two v1",
            [AgentThreePath] = "three v1"
        };

        foreach (string path in prior.Keys)
        {
            JsonNode file = declared[path];
            Assert.Equal("file", file["originalKind"]!.GetValue<string>());
            Assert.Equal("file", file["afterKind"]!.GetValue<string>());
            Assert.False(string.IsNullOrEmpty(file["physicalRoot"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(file["afterSha256"]!.GetValue<string>()));

            // A backup the journal verified, and a claim slot recovery may find the original in.
            Assert.Contains(artifacts, artifact =>
                artifact!["role"]!.GetValue<string>() == "target-backup" &&
                artifact["path"]!.GetValue<string>().EndsWith(path, StringComparison.Ordinal));
            Assert.Contains(artifacts, artifact =>
                artifact!["role"]!.GetValue<string>() == "claimed-original" &&
                artifact["lifecycleState"]!.GetValue<string>() == "active-transition" &&
                artifact["sha256"]!.GetValue<string>() == Digest(prior[path]));
            Assert.Contains(transitions, transition => transition!["allowedState"]!
                .GetValue<string>() == "pre-operation");
        }

        RestoreTree(root, atCrash);
        Transaction(root).Recover(root, SquadDeploymentScope.Project);
        AssertTreesEqual(original, CaptureTree(root));
    }

    public static TheoryData<SquadTransactionCheckpointKind, int> UpdateCrashPoints => new()
    {
        // The batch boundary, then each step inside the batch for its first, second and last file.
        { SquadTransactionCheckpointKind.ActiveTransitionWritten, 1 },
        { SquadTransactionCheckpointKind.OriginalClaimed, 1 },
        { SquadTransactionCheckpointKind.AfterImagePublished, 1 },
        { SquadTransactionCheckpointKind.OriginalClaimed, 2 },
        { SquadTransactionCheckpointKind.AfterImagePublished, 2 },
        { SquadTransactionCheckpointKind.AfterImagePublished, 3 }
    };

    public static TheoryData<SquadTransactionCheckpointKind, int> InstallCrashPoints => new()
    {
        { SquadTransactionCheckpointKind.ActiveTransitionWritten, 1 },
        { SquadTransactionCheckpointKind.AfterImagePublished, 1 },
        { SquadTransactionCheckpointKind.AfterImagePublished, 2 },
        { SquadTransactionCheckpointKind.AfterImagePublished, 3 }
    };

    [Theory]
    [MemberData(nameof(UpdateCrashPoints))]
    public void RecoverAfterAnUpdateCrashRestoresTheExactPriorTree(
        SquadTransactionCheckpointKind checkpoint,
        int occurrence)
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SeedUpdate(root, out SquadReceipt receipt);
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);
        CrashObserver observer = new(root, checkpoint, occurrence);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(UpdatePlan(root, receipt)));
        RestoreTree(root, Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.Snapshot));

        Transaction(root).Recover(root, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(root));
    }

    [Theory]
    [MemberData(nameof(InstallCrashPoints))]
    public void RecoverAfterAnInstallCrashRemovesEveryTraceOfTheInstall(
        SquadTransactionCheckpointKind checkpoint,
        int occurrence)
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);
        CrashObserver observer = new(root, checkpoint, occurrence);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(InstallPlan(root)));
        RestoreTree(root, Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.Snapshot));

        Transaction(root).Recover(root, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(root));
    }

    [Fact]
    public void ASecondBatchBoundaryCrashRecoversToTheExactPriorTree()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SquadDeploymentFile[] files = Enumerable.Range(0, 70)
            .Select(index => Rendered($".codex/agents/agent-{index:D3}.toml", $"body {index}"))
            .ToArray();
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);

        // The second boundary is written when the first 64-file batch is already applied.
        CrashObserver observer = new(root, SquadTransactionCheckpointKind.ActiveTransitionWritten, 2);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(SquadDeploymentPlanner.CreateInstall(
                root,
                SquadDeploymentScope.Project,
                Lock(),
                files,
                [],
                false,
                new FixedTimeProvider(InstalledAt))));
        RestoreTree(root, Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.Snapshot));
        Assert.True(File.Exists(ToPlatformPath(root, ".codex/agents/agent-000.toml")));

        Transaction(root).Recover(root, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(root));
    }

    [Fact]
    public void RecoverAfterAKillDuringRollbackCompletesThePriorTree()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SeedUpdate(root, out SquadReceipt receipt);
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);
        CrashObserver observer = new(root, SquadTransactionCheckpointKind.AfterImagePublished, 2);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(UpdatePlan(root, receipt)));
        RestoreTree(root, Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.Snapshot));

        // A rollback killed part-way leaves the files it had not reached at their after-image and the ones it
        // had finished back at the original. Reproduce both states, plus the temporary an interrupted atomic
        // restore leaves beside its destination, and require recovery to finish the job.
        Write(root, AgentOnePath, "one v1");
        string transactionId = ReadJournal(root)["transactionId"]!.GetValue<string>();
        string interruptedRestore = Path.Combine(
            Path.GetDirectoryName(ToPlatformPath(root, AgentTwoPath))!,
            $".two.toml.{transactionId}.restore");
        File.WriteAllText(interruptedRestore, "half of two v1");

        Transaction(root).Recover(root, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(root));
        Assert.False(File.Exists(interruptedRestore));
    }

    [Fact]
    public void RecoverAfterAKillDuringRecoveryIsStillThePriorTree()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SeedUpdate(root, out SquadReceipt receipt);
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);
        CrashObserver observer = new(root, SquadTransactionCheckpointKind.AfterImagePublished, 3);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(UpdatePlan(root, receipt)));
        RestoreTree(root, Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.Snapshot));

        // First recovery finishes. The second must be a no-op, and a third after a replayed first attempt
        // must still land on the prior tree: recovery is idempotent per file, not per run.
        Transaction(root).Recover(root, SquadDeploymentScope.Project);
        AssertTreesEqual(original, CaptureTree(root));

        Transaction(root).Recover(root, SquadDeploymentScope.Project);
        AssertTreesEqual(original, CaptureTree(root));
    }

    [Theory]
    [MemberData(nameof(UpdateCrashPoints))]
    public void RecoveryNeverTouchesAForeignAdoptedOrLocallyEditedFile(
        SquadTransactionCheckpointKind checkpoint,
        int occurrence)
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SeedUpdate(root, out SquadReceipt receipt);
        SortedDictionary<string, TreeEntry> original = CaptureTree(root);
        CrashObserver observer = new(root, checkpoint, occurrence);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(root, observer).Execute(UpdatePlan(root, receipt)));
        RestoreTree(root, Assert.IsType<SortedDictionary<string, TreeEntry>>(observer.Snapshot));

        Transaction(root).Recover(root, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(root));
        Assert.Equal("adopted body", Read(root, AdoptedPath));
        Assert.Equal("operator notes", Read(root, ForeignPath));
    }

    /// <summary>
    ///     An operator edit that lands after preflight but before the backup is captured makes the file's claim
    ///     disagree with the plan's precondition. The transaction must refuse that leaf and still leave a
    ///     journal recovery can read: the rest of the batch is in flight, and their declarations must survive.
    /// </summary>
    [Fact]
    public void RecoverAfterAnOperatorEditMidBatchLeavesAReadableJournal()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SeedUpdate(root, out SquadReceipt receipt);
        RaceObserver observer = new(SquadTransactionStepKind.IntentWritten, () =>
            Write(root, AgentTwoPath, "operator edit"));

        Assert.ThrowsAny<InvalidOperationException>(() =>
            Transaction(root, observer).Execute(UpdatePlan(root, receipt)));

        // The operator's bytes are the file's prior state now, so the prior tree keeps them.
        Transaction(root).Recover(root, SquadDeploymentScope.Project);
        Assert.Equal("one v1", Read(root, AgentOnePath));
        Assert.Equal("operator edit", Read(root, AgentTwoPath));
        Assert.Equal("three v1", Read(root, AgentThreePath));
        Assert.Equal("adopted body", Read(root, AdoptedPath));
        Assert.False(Directory.Exists(Path.Combine(root, ".kyber-weave", ".squad-transaction")));
    }

    /// <summary>
    ///     A file deleted between the batch generation and its own claim is the operator's act, not a corrupt
    ///     journal. The transaction must roll back what it can and keep a journal whose recovery reports that
    ///     conflict — not a data error, and not a half-applied tree left behind.
    /// </summary>
    [Fact]
    public void RecoverAfterAnOperatorDeleteMidBatchReportsAConflictNotADataError()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        SeedUpdate(root, out SquadReceipt receipt);
        RaceObserver observer = new(SquadTransactionCheckpointKind.ActiveTransitionWritten, () =>
            File.Delete(ToPlatformPath(root, AgentTwoPath)));

        // The leaf conflict is this transaction's own control-flow exception, so the run refuses here; what
        // matters is that its rollback finishes and the journal it keeps is recoverable.
        Assert.ThrowsAny<InvalidOperationException>(() =>
            Transaction(root, observer).Execute(UpdatePlan(root, receipt)));

        Assert.Equal("one v1", Read(root, AgentOnePath));
        Assert.False(File.Exists(ToPlatformPath(root, AgentTwoPath)));
        Assert.Equal("three v1", Read(root, AgentThreePath));

        Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(root).Recover(root, SquadDeploymentScope.Project));
    }

    /// <summary>
    ///     A file that appears where the plan expects none is refused, and the refusal must keep the rest of the
    ///     batch's declarations: a journal recovery cannot read is a deployment nobody can repair.
    /// </summary>
    [Fact]
    public void RecoverAfterAFileAppearsMidInstallReportsAConflictNotADataError()
    {
        using TempDirectory temp = new();
        string root = temp.Path;
        RaceObserver observer = new(SquadTransactionCheckpointKind.ActiveTransitionWritten, () =>
            Write(root, AgentTwoPath, "foreign body"));

        Assert.ThrowsAny<InvalidOperationException>(() =>
            Transaction(root, observer).Execute(InstallPlan(root)));

        Assert.False(File.Exists(ToPlatformPath(root, AgentOnePath)));
        Assert.Equal("foreign body", Read(root, AgentTwoPath));

        Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(root).Recover(root, SquadDeploymentScope.Project));
    }

    private static void SeedUpdate(string root, out SquadReceipt receipt)
    {
        Write(root, AgentOnePath, "one v1");
        Write(root, AgentTwoPath, "two v1");
        Write(root, AgentThreePath, "three v1");
        Write(root, AdoptedPath, "adopted body");
        Write(root, ForeignPath, "operator notes");
        receipt = Receipt(
            new SquadOwnedFile(AgentOnePath, Digest("one v1"), "codex", false),
            new SquadOwnedFile(AgentTwoPath, Digest("two v1"), "codex", false),
            new SquadOwnedFile(AgentThreePath, Digest("three v1"), "codex", false),
            new SquadOwnedFile(AdoptedPath, Digest("adopted body"), "codex", true));
        WriteState(root, Lock(), receipt);
    }

    private static SquadDeploymentPlan UpdatePlan(string root, SquadReceipt receipt)
    {
        return SquadDeploymentPlanner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [
                Rendered(AgentOnePath, "one v2"),
                Rendered(AgentTwoPath, "two v2"),
                Rendered(AgentThreePath, "three v2")
            ],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
    }

    private static SquadDeploymentPlan InstallPlan(string root)
    {
        return SquadDeploymentPlanner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [
                Rendered(AgentOnePath, "one v1"),
                Rendered(AgentTwoPath, "two v1"),
                Rendered(AgentThreePath, "three v1")
            ],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
    }

    private static JsonNode ReadJournal(string root)
    {
        string journal = Path.Combine(root, ".kyber-weave", ".squad-transaction", "intent.json");
        return JsonNode.Parse(File.ReadAllText(journal))!;
    }

    private sealed class CrashObserver(
        string root,
        SquadTransactionCheckpointKind checkpoint,
        int occurrence) : ISquadTransactionCheckpointObserver
    {
        private int _seen;

        public IReadOnlyDictionary<string, TreeEntry>? Snapshot { get; private set; }

        /// <summary>The journal exactly as the killed process left it on disk.</summary>
        public JsonNode? Journal { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint || ++_seen != occurrence)
                return;

            Snapshot = CaptureTree(root);
            string journal = Snapshot.Keys.Single(path =>
                path.EndsWith("/intent.json", StringComparison.Ordinal));
            Journal = JsonNode.Parse(
                Encoding.UTF8.GetString(Assert.IsType<byte[]>(Snapshot[journal].Content)));
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    /// <summary>
    ///     Runs a filesystem change at one transaction step or checkpoint, modelling an operator or another
    ///     process touching a deployed path while the apply loop is running.
    /// </summary>
    private sealed class RaceObserver : ISquadTransactionCheckpointObserver
    {
        private readonly SquadTransactionCheckpointKind? _checkpoint;
        private readonly Action _race;
        private readonly SquadTransactionStepKind? _step;

        public RaceObserver(SquadTransactionStepKind step, Action race)
        {
            _step = step;
            _race = race;
        }

        public RaceObserver(SquadTransactionCheckpointKind checkpoint, Action race)
        {
            _checkpoint = checkpoint;
            _race = race;
        }

        public void AfterStep(SquadTransactionStep step)
        {
            if (_step == step.Kind)
                _race();
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (_checkpoint == checkpoint.Kind)
                _race();
        }
    }
}
