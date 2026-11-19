using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// The transaction and recovery fixtures of <see cref="InMemoryDeploymentStateTestBase" />: the JSON authority helpers,
/// the scenario builders and the observers that stand at a checkpoint and tamper with the tree. Each one reads and writes
/// through the in-memory file system, so the case that uses it needs no disk.
/// </summary>
public abstract partial class InMemoryDeploymentStateTestBase
{
    /// <summary>Builds the global install plan the created-directory cases recover.</summary>
    /// <param name="targetRoot">The target root.</param>
    /// <returns>The plan.</returns>
    internal SquadDeploymentPlan CreateGlobalInstallPlan(string targetRoot) =>
        Planner.CreateInstall(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "global conductor")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));

    /// <summary>Asserts a refused transaction names the active lease.</summary>
    /// <param name="exception">The refusal.</param>
    internal static void AssertActiveLeaseConflict(Exception? exception)
    {
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fails a transaction after one lifecycle step and returns the tree it left behind.</summary>
    /// <param name="fixture">The project fixture.</param>
    /// <param name="checkpoint">The step to fail after.</param>
    /// <returns>The tree at the interrupted boundary.</returns>
    internal SortedDictionary<string, TreeEntry> InterruptTransactionAfter(
        TransactionFixture fixture,
        SquadTransactionStepKind checkpoint)
    {
        LifecycleStepFailingObserver observer = new(Files, fixture.Path, checkpoint);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            TransactionAt(fixture.Path, observer).Execute(fixture.CreateUpdatePlan()));

        return Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
    }

    /// <summary>Fails a transaction after one checkpoint and returns the tree it left behind.</summary>
    /// <param name="fixture">The project fixture.</param>
    /// <param name="checkpoint">The checkpoint to fail after.</param>
    /// <returns>The tree at the interrupted boundary.</returns>
    internal SortedDictionary<string, TreeEntry> InterruptTransactionAfterCheckpoint(
        TransactionFixture fixture,
        SquadTransactionCheckpointKind checkpoint)
    {
        CheckpointFailingObserver observer = new(Files, fixture.Path, checkpoint);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            TransactionAt(fixture.Path, observer).Execute(fixture.CreateUpdatePlan()));

        return Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
    }

    /// <summary>Captures a tree without the retained transaction evidence.</summary>
    /// <param name="root">The root.</param>
    /// <returns>The tree.</returns>
    internal SortedDictionary<string, TreeEntry> CaptureTreeWithoutTransactionEvidence(string root)
    {
        SortedDictionary<string, TreeEntry> result = CaptureTree(root);
        foreach (string path in result.Keys.Where(path =>
                     path.Equals(
                         ".kyber-weave/.squad-transaction",
                         StringComparison.Ordinal) ||
                     path.StartsWith(
                         ".kyber-weave/.squad-transaction/",
                         StringComparison.Ordinal)).ToArray())
            result.Remove(path);

        return result;
    }

    /// <summary>Asserts every prepared artifact names the transaction, area and role that produced it.</summary>
    /// <param name="artifacts">The artifact authority array.</param>
    internal static void AssertPreparedArtifactAuthority(JsonArray artifacts)
    {
        foreach (JsonNode? node in artifacts)
        {
            JsonObject artifact = Assert.IsType<JsonObject>(node);
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["transactionId"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["area"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["role"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["nodeKind"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["lifecycleState"]).GetValue<string>()));
        }
    }

    /// <summary>Asserts the created-directory authority is canonical, unique and deepest first.</summary>
    /// <param name="createdDirectories">The created-directory authority array.</param>
    internal static void AssertCreatedDirectoryAuthorityIsCanonical(JsonArray createdDirectories)
    {
        int previousDepth = int.MaxValue;
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (JsonNode? node in createdDirectories)
        {
            JsonObject authority = Assert.IsType<JsonObject>(node);
            string area = Assert.IsAssignableFrom<JsonValue>(authority["area"]).GetValue<string>();
            string path = Assert.IsAssignableFrom<JsonValue>(authority["path"]).GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(area));
            Assert.False(Path.IsPathRooted(path));
            Assert.DoesNotContain("..", path.Split('/'), StringComparer.Ordinal);
            Assert.True(identities.Add($"{area}:{path}"));
            int depth = path.Count(character => character == '/');
            Assert.True(
                depth <= previousDepth,
                "Created-directory authority must be serialized deepest first.");
            previousDepth = depth;
        }
    }

    /// <summary>Corrupts the created-directory authority of a prepared journal one way.</summary>
    /// <param name="intent">The journal.</param>
    /// <param name="createdDirectories">The created-directory authority array.</param>
    /// <param name="fixtureRoot">The root the journal describes.</param>
    /// <param name="corruption">The corruption to make.</param>
    internal static void CorruptCreatedDirectoryAuthority(
        JsonObject intent,
        JsonArray createdDirectories,
        string fixtureRoot,
        string corruption)
    {
        JsonObject first = Assert.IsType<JsonObject>(createdDirectories[0]);
        switch (corruption)
        {
            case "wrong-area":
                first["area"] = "outside";
                break;
            case "non-ancestor":
                first["path"] = "unrelated/not-an-ancestor";
                break;
            case "duplicate":
                createdDirectories.Add(first.DeepClone());
                break;
            case "unordered":
                Assert.True(createdDirectories.Count > 1);
                JsonObject last = Assert.IsType<JsonObject>(createdDirectories[^1]);
                createdDirectories.RemoveAt(createdDirectories.Count - 1);
                createdDirectories.Insert(0, last);
                break;
            case "absolute":
                first["path"] = Path.Combine(fixtureRoot, "absolute");
                break;
            case "traversal":
                first["path"] = "../escape";
                break;
            case "sibling":
                first["path"] = "beside-target";
                break;
            case "outside-root":
                first["path"] = Path.GetFullPath(Path.Combine(fixtureRoot, "..", "outside"));
                break;
            case "legacy-count":
                intent["missingJournalDirectoryCount"] = 1;
                break;
            case "negative-count":
                intent["missingJournalDirectoryCount"] = -1;
                break;
            case "maximum-count":
                intent["missingJournalDirectoryCount"] = int.MaxValue;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    /// <summary>Corrupts the prepared artifact authority of a journal one way.</summary>
    /// <param name="intent">The journal.</param>
    /// <param name="transactionDirectory">The journal directory.</param>
    /// <param name="corruption">The corruption to make.</param>
    internal void CorruptPreparedAuthority(
        JsonObject intent,
        string transactionDirectory,
        string corruption)
    {
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        JsonObject first = Assert.IsType<JsonObject>(artifacts[0]);
        switch (corruption)
        {
            case "missing-required":
                artifacts.RemoveAt(0);
                break;
            case "undeclared-file":
                Write(transactionDirectory, "undeclared.bin", "undeclared file");
                break;
            case "undeclared-link":
                Files.CreateFileLink(
                    Path.Combine(transactionDirectory, "undeclared-link"),
                    "missing-link-target");
                break;
            case "undeclared-directory":
                // An empty undescribed folder is what a kill leaves and recovery removes; one holding a file is not.
                Files.CreateDirectory(Path.Combine(transactionDirectory, "undeclared-directory"));
                Files.WriteAllText(
                    Path.Combine(transactionDirectory, "undeclared-directory", "stray.txt"),
                    "stray");
                break;
            case "duplicate-identity":
                artifacts.Add(first.DeepClone());
                break;
            case "swapped-area":
                first["area"] = first["area"]?.GetValue<string>() == "journal"
                    ? "work"
                    : "journal";
                break;
            case "swapped-role":
                first["role"] = "state-stage";
                break;
            case "swapped-lifecycle":
                first["lifecycleState"] = "active-transition";
                break;
            case "invalid-transition":
                first["lifecycleState"] = "post-operation";
                intent["activeTransition"] = new JsonObject
                {
                    ["artifactIdentity"] = "not-the-mutated-artifact",
                    ["allowedState"] = "pre-operation"
                };
                break;
            case "well-digested-unexpected":
                const string unexpectedPath = "unexpected-authority.bin";
                byte[] unexpectedBytes = "well-digested but undeclared"u8.ToArray();
                Files.WriteAllBytes(Path.Combine(transactionDirectory, unexpectedPath), unexpectedBytes);
                JsonObject unexpected = Assert.IsType<JsonObject>(first.DeepClone());
                unexpected["area"] = "journal";
                unexpected["path"] = unexpectedPath;
                unexpected["byteLength"] = unexpectedBytes.LongLength;
                unexpected["sha256"] = Digest(unexpectedBytes);
                artifacts.Add(unexpected);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    /// <summary>Adds or removes one semantic artifact, manifest entry and tree node together.</summary>
    /// <param name="intent">The journal.</param>
    /// <param name="transactionDirectory">The journal directory.</param>
    /// <param name="edit">Whether to <c>add</c> or <c>remove</c>.</param>
    /// <param name="semanticRole">The artifact role.</param>
    internal void EditSemanticArtifactSet(
        JsonObject intent,
        string transactionDirectory,
        string edit,
        string semanticRole)
    {
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        if (edit == "remove")
        {
            (JsonObject Artifact, int Index) match = artifacts
                .Select((node, index) => (
                    Artifact: Assert.IsType<JsonObject>(node),
                    Index: index))
                .First(pair => string.Equals(
                    pair.Artifact["role"]?.GetValue<string>(),
                    semanticRole,
                    StringComparison.Ordinal));
            string removedRelativePath = Assert.IsType<string>(
                match.Artifact["path"]?.GetValue<string>());
            Files.DeleteTreeNoFollow(ToPlatformPath(transactionDirectory, removedRelativePath));
            artifacts.RemoveAt(match.Index);
            RemoveEmptyTestArtifactParents(
                ToPlatformPath(transactionDirectory, removedRelativePath),
                transactionDirectory);
            return;
        }

        if (edit != "add")
            throw new ArgumentOutOfRangeException(nameof(edit), edit, null);

        string transactionId = Assert.IsType<string>(intent["transactionId"]?.GetValue<string>());
        string suffix = Digest($"{transactionId}:{semanticRole}");
        (string area, string relativePath) = semanticRole switch
        {
            "target-stage" => ("work", $"staging/semantic-extra-{suffix}.bin"),
            "target-backup" => ("work", $"backups/semantic-extra-{suffix}.bin"),
            "target-link-metadata" => ("work", $"links/semantic-extra-{suffix}.txt"),
            "state-stage" => ("journal", $"state-staging/semantic-extra-{suffix}"),
            "state-original" => ("work", $"state-originals/semantic-extra-{suffix}"),
            "claimed-original" => ("work", $"claimed-{transactionId}-{suffix}"),
            "discarded-after-image" => ("work", $"discarded-{transactionId}-{suffix}"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(semanticRole),
                semanticRole,
                null)
        };
        byte[] bytes = Encoding.UTF8.GetBytes($"role-valid semantic extra {semanticRole}");
        string fullPath = ToPlatformPath(transactionDirectory, relativePath);
        Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        Files.WriteAllBytes(fullPath, bytes);
        artifacts.Add(new JsonObject
        {
            ["transactionId"] = transactionId,
            ["area"] = area,
            ["role"] = semanticRole,
            ["path"] = relativePath,
            ["nodeKind"] = "file",
            ["byteLength"] = bytes.LongLength,
            ["sha256"] = Digest(bytes),
            ["linkTarget"] = string.Empty,
            ["lifecycleState"] = "pre-operation"
        });
    }

    /// <summary>Removes a node and then every ancestor of it the removal emptied.</summary>
    /// <param name="artifactPath">The removed node.</param>
    /// <param name="root">The directory the walk stops at.</param>
    private void RemoveEmptyTestArtifactParents(string artifactPath, string root)
    {
        string? current = Path.GetDirectoryName(artifactPath);
        while (current is not null &&
               !string.Equals(current, root, StringComparison.Ordinal) &&
               Files.DirectoryExists(current) &&
               !Files.EnumerateEntries(current).Any())
        {
            Files.DeleteEmptyDirectory(current);
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>Corrupts one prepared artifact of an interrupted transaction.</summary>
    /// <param name="root">The project root.</param>
    /// <param name="relativePath">The deployed file the transaction was publishing.</param>
    /// <param name="corruption">The corruption to make.</param>
    internal void CorruptPreparedArtifact(
        string root,
        string relativePath,
        string corruption)
    {
        string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        string backupPath = ToPlatformPath(
            Path.Combine(transactionDirectory, "backups"),
            $"codex/{relativePath}");
        switch (corruption)
        {
            case "truncated-journal":
                byte[] journal = Files.ReadAllBytes(intentPath);
                Files.WriteAllBytes(intentPath, journal[..Math.Max(1, journal.Length / 2)]);
                break;
            case "duplicate-journal-field":
                Files.WriteAllText(
                    intentPath,
                    CorruptJournalDocument(
                        Files.ReadAllText(intentPath),
                        "duplicate"));
                break;
            case "partial-backup":
                byte[] backup = Files.ReadAllBytes(backupPath);
                Files.WriteAllBytes(backupPath, backup[..Math.Max(1, backup.Length / 2)]);
                break;
            case "wrong-backup-digest":
                byte[] wrong = Files.ReadAllBytes(backupPath);
                wrong[0] ^= 0xff;
                Files.WriteAllBytes(backupPath, wrong);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    /// <summary>Replaces one published artifact with a partial temporary file of the same name.</summary>
    /// <param name="root">The project root.</param>
    /// <param name="relativePath">The deployed file the transaction was publishing.</param>
    /// <param name="artifact">Which artifact: <c>journal</c>, <c>staging</c> or <c>backup</c>.</param>
    internal void ReplacePublishedArtifactWithPartialTemporaryFile(
        string root,
        string relativePath,
        string artifact)
    {
        string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
        string publishedPath = artifact switch
        {
            "journal" => Path.Combine(transactionDirectory, "intent.json"),
            "staging" => ToPlatformPath(
                Path.Combine(transactionDirectory, "staging"),
                $"codex/{relativePath}"),
            "backup" => ToPlatformPath(
                Path.Combine(transactionDirectory, "backups"),
                $"codex/{relativePath}"),
            _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact, null)
        };
        byte[] bytes = Files.ReadAllBytes(publishedPath);
        string temporaryPath = publishedPath + ".tmp";
        Files.MoveFile(publishedPath, temporaryPath);
        Files.WriteAllBytes(temporaryPath, bytes[..Math.Max(1, bytes.Length / 2)]);
    }

    /// <summary>Builds a project whose live target or state file is the node kind an active-transition case names.</summary>
    /// <param name="root">The project root.</param>
    /// <param name="scenario">The operation and node kind the case names.</param>
    /// <returns>The plan and the subject it publishes.</returns>
    internal ActiveTransitionScenario CreateActiveTransitionScenario(
        string root,
        string scenario)
    {
        const string relativePath = ".codex/agents/conductor.toml";
        if (scenario is "file-write" or "file-delete")
        {
            const string installed = "installed file body";
            Write(root, relativePath, installed);
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest(installed),
                "codex",
                false));
            WriteState(root, Lock(), receipt);
            SquadDeploymentFile[] rendered = scenario == "file-write"
                ? [Rendered(relativePath, "updated file body")]
                : [];
            return new ActiveTransitionScenario(
                Planner.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    rendered,
                    receipt,
                    [],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                relativePath);
        }

        if (scenario is "link-write" or "link-delete")
        {
            const string installed = "installed link target";
            const string targetRelativePath = "canonical/conductor.toml";
            string canonicalPath = Path.Combine(root, "canonical", "conductor.toml");
            Write(root, targetRelativePath, installed);
            string fullLinkPath = ToPlatformPath(root, relativePath);
            Files.CreateDirectory(Path.GetDirectoryName(fullLinkPath)!);
            Files.CreateFileLink(
                fullLinkPath,
                Path.GetRelativePath(Path.GetDirectoryName(fullLinkPath)!, canonicalPath));
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest(installed),
                "codex",
                false));
            WriteState(root, Lock(), receipt);
            SquadDeploymentFile[] rendered = scenario == "link-write"
                ? [Rendered(relativePath, "updated link body")]
                : [];
            return new ActiveTransitionScenario(
                Planner.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    rendered,
                    receipt,
                    [],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                relativePath);
        }

        SquadReceipt emptyReceipt = Receipt();
        WriteState(root, Lock(), emptyReceipt);
        if (scenario is "lock-write" or "receipt-write")
            return new ActiveTransitionScenario(
                Planner.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    [],
                    emptyReceipt,
                    [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                scenario.StartsWith("lock", StringComparison.Ordinal) ? "lock" : "receipt");

        if (scenario is "lock-delete" or "receipt-delete")
            return new ActiveTransitionScenario(
                Planner.CreateUninstall(
                    root,
                    SquadDeploymentScope.Project,
                    emptyReceipt),
                scenario.StartsWith("lock", StringComparison.Ordinal) ? "lock" : "receipt");

        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
    }

    /// <summary>Asserts a claim edited at the claim checkpoint is caught and its evidence retained.</summary>
    /// <param name="subject">The artifact: <c>target</c>, <c>lock</c> or <c>receipt</c>.</param>
    /// <param name="originalKind">The node kind the live artifact had.</param>
    /// <param name="mutation">The mutation to make.</param>
    internal void AssertCaughtClaimMutation(
        string subject,
        string originalKind,
        string mutation)
    {
        DeploymentScope fixture = NewScope();
        string fixturePath = fixture.Path;
        ClaimMutationScenario scenario = CreateClaimMutationScenario(
            fixturePath,
            subject,
            originalKind);
        SortedDictionary<string, TreeEntry> original = CaptureTree(fixturePath);
        OriginalClaimTamperingObserver observer = new(
            Files,
            fixturePath,
            scenario.Subject,
            mutation);
        SquadTransaction transaction = TransactionAt(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(scenario.Plan));

        Assert.True(
            observer.SawOriginalClaimed,
            $"The original claim checkpoint was not reached; the run ended with: {exception?.GetType().Name}: {exception?.Message}");
        Assert.False(observer.SawAffectedAfterImage);
        Assert.False(observer.SawAffectedPublicApply);
        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Assert.IsType<InjectedSquadTransactionFailure>(exception);
            AssertTreesEqual(original, CaptureTree(fixturePath));
            Assert.False(Files.DirectoryExists(Path.Combine(
                fixturePath,
                ".kyber-weave",
                ".squad-transaction")));
            return;
        }

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("claim", invalid.Message, StringComparison.OrdinalIgnoreCase);
        SortedDictionary<string, TreeEntry> caughtTree = CaptureTree(fixturePath);
        Assert.False(caughtTree.ContainsKey(scenario.LiveRelativePath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedClaimEntry),
            caughtTree[observer.ClaimRelativePath]);
        Assert.True(Files.FileExists(observer.IntentPath));

        SquadTransaction recoveryTransaction = TransactionAt(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(caughtTree, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    /// <summary>Asserts a claim edited at the claim checkpoint is caught again after rehydration.</summary>
    /// <param name="subject">The artifact: <c>target</c>, <c>lock</c> or <c>receipt</c>.</param>
    /// <param name="originalKind">The node kind the live artifact had.</param>
    /// <param name="mutation">The mutation to make.</param>
    internal void AssertRehydratedClaimMutation(
        string subject,
        string originalKind,
        string mutation)
    {
        DeploymentScope fixture = NewScope();
        string fixturePath = fixture.Path;
        ClaimMutationScenario scenario = CreateClaimMutationScenario(
            fixturePath,
            subject,
            originalKind);
        SortedDictionary<string, TreeEntry> original = CaptureTree(fixturePath);
        OriginalClaimTamperingObserver observer = new(
            Files,
            fixturePath,
            scenario.Subject,
            mutation);
        SquadTransaction transaction = TransactionAt(fixturePath, observer);
        _ = Record.Exception(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            TransactionAt(fixturePath).Recover(
                fixturePath,
                SquadDeploymentScope.Project);
            SortedDictionary<string, TreeEntry> controlAfterFirstRecovery = CaptureTree(fixturePath);
            TransactionAt(fixturePath).Recover(
                fixturePath,
                SquadDeploymentScope.Project);

            AssertTreesEqual(original, controlAfterFirstRecovery);
            AssertTreesEqual(controlAfterFirstRecovery, CaptureTree(fixturePath));
            return;
        }

        SortedDictionary<string, TreeEntry> interrupted = CaptureTree(fixturePath);
        Assert.False(interrupted.ContainsKey(scenario.LiveRelativePath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedClaimEntry),
            interrupted[observer.ClaimRelativePath]);

        SquadTransaction recoveryTransaction = TransactionAt(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(interrupted, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    /// <summary>An active-transition plan and the subject it publishes.</summary>
    /// <param name="Plan">The plan.</param>
    /// <param name="Subject">The published artifact.</param>
    internal sealed record ActiveTransitionScenario(
        SquadDeploymentPlan Plan,
        string Subject);

    /// <summary>Blocks inside one lifecycle step so a test can act while the transaction is active.</summary>
    internal sealed class BlockingObserver(SquadTransactionStepKind blockedStep) :
        ISquadTransactionObserver,
        IDisposable
    {
        /// <summary>Gets the signal set when the blocked step is reached.</summary>
        public ManualResetEventSlim Reached { get; } = new(false);

        /// <summary>Gets the signal the test sets to let the transaction continue.</summary>
        public ManualResetEventSlim Release { get; } = new(false);

        /// <inheritdoc />
        public void Dispose()
        {
            Reached.Dispose();
            Release.Dispose();
        }

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != blockedStep)
                return;

            Reached.Set();
            Release.Wait();
        }
    }

    /// <summary>Stops the run after one checkpoint, capturing the tree the process would leave behind.</summary>
    internal sealed class CheckpointFailingObserver(
        InMemoryFileSystem files,
        string root,
        SquadTransactionCheckpointKind checkpoint) : ISquadTransactionCheckpointObserver
    {
        /// <summary>Gets the tree captured at the failed checkpoint.</summary>
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
        }

        /// <inheritdoc />
        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint)
                return;

            InterruptedTreeSnapshot = CaptureTree(files, root);
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    /// <summary>Stops the run at one checkpoint of one artifact, capturing the interrupted tree.</summary>
    internal sealed class ActiveTransitionCrashObserver(
        InMemoryFileSystem files,
        string root,
        SquadTransactionCheckpointKind checkpoint,
        string subject) : ISquadTransactionCheckpointObserver
    {
        /// <summary>Gets the tree captured at the failed checkpoint.</summary>
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
        }

        /// <inheritdoc />
        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint ||
                !string.Equals(value.RelativePath, subject, StringComparison.Ordinal))
                return;

            InterruptedTreeSnapshot = CaptureTree(files, root);
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    /// <summary>Replaces a claimed leaf with another node kind once the transaction has claimed it.</summary>
    internal sealed class LeafRaceObserver(
        InMemoryFileSystem files,
        string root,
        string relativePath,
        string racedNodeKind) : ISquadTransactionCheckpointObserver
    {
        private bool _raced;

        /// <summary>Gets the raced entry as the tree held it.</summary>
        public TreeEntry? RacedEntry { get; private set; }

        /// <summary>Gets the path the operator wrote outside the deployment.</summary>
        public string ExternalCanaryPath { get; private set; } = string.Empty;

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
        }

        /// <inheritdoc />
        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (_raced || checkpoint.Kind != SquadTransactionCheckpointKind.Prepared)
                return;

            string targetPath = ToPlatformPath(root, relativePath);
            if (files.PathExists(targetPath) || files.GetLinkTarget(targetPath) is not null)
                files.DeleteTreeNoFollow(targetPath);
            files.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            switch (racedNodeKind)
            {
                case "file":
                    files.WriteAllText(targetPath, "external raced file");
                    ExternalCanaryPath = targetPath;
                    break;
                case "directory":
                    files.CreateDirectory(targetPath);
                    ExternalCanaryPath = Path.Combine(targetPath, "external-child.txt");
                    files.WriteAllText(ExternalCanaryPath, "external raced directory child");
                    break;
                case "link":
                    ExternalCanaryPath = Path.Combine(root, "external-link-target.txt");
                    files.WriteAllText(ExternalCanaryPath, "external raced link target");
                    files.CreateFileLink(targetPath, ExternalCanaryPath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown raced node kind '{racedNodeKind}'.");
            }

            RacedEntry = CaptureTree(files, root)[relativePath];
            _raced = true;
        }
    }

    /// <summary>Changes one prepared stage after its own publication, before the first apply.</summary>
    internal sealed class StagedArtifactTamperingObserver(
        InMemoryFileSystem files,
        string root,
        string relativePath,
        string artifact) : ISquadTransactionCheckpointObserver
    {
        private bool _tampered;

        /// <summary>Gets whether the affected artifact's public apply was reached.</summary>
        public bool SawAffectedApplyCheckpoint { get; private set; }

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedApplyCheckpoint |= artifact switch
            {
                "target-file" => step.Kind == SquadTransactionStepKind.FileApplied,
                "lock-state" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt-state" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => throw new InvalidOperationException(
                    $"Unknown staged artifact contract '{artifact}'.")
            };
        }

        /// <inheritdoc />
        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!_tampered && checkpoint.Kind == SquadTransactionCheckpointKind.Prepared)
            {
                string transactionDirectory = Path.Combine(
                    root,
                    ".kyber-weave",
                    ".squad-transaction");
                string stagePath = artifact switch
                {
                    "target-file" => ToPlatformPath(
                        Path.Combine(transactionDirectory, "staging"),
                        $"codex/{relativePath}"),
                    "lock-state" => Path.Combine(
                        transactionDirectory,
                        "state-staging",
                        "lock"),
                    "receipt-state" => Path.Combine(
                        transactionDirectory,
                        "state-staging",
                        "receipt"),
                    _ => throw new InvalidOperationException(
                        $"Unknown staged artifact contract '{artifact}'.")
                };
                Assert.True(files.FileExists(stagePath), $"Prepared stage '{stagePath}' was not published.");
                files.WriteAllText(
                    stagePath,
                    $"changed prepared {artifact}");
                _tampered = true;
            }
        }
    }

    /// <summary>Corrupts the prepared artifact generation once it is complete.</summary>
    internal sealed class PreparedBoundaryCorruptionObserver(
        InMemoryFileSystem files,
        string root,
        string relativePath,
        string corruption) : ISquadTransactionCheckpointObserver
    {
        /// <summary>Gets whether the prepared generation was corrupted.</summary>
        public bool CorruptedPreparedArtifacts { get; private set; }

        /// <summary>Gets whether any leaf or state mutation was reached after the corruption.</summary>
        public bool SawLeafOrStateMutation { get; private set; }

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            if (!CorruptedPreparedArtifacts)
                return;

            SawLeafOrStateMutation |= step.Kind is
                SquadTransactionStepKind.FileApplied or
                SquadTransactionStepKind.LockApplied or
                SquadTransactionStepKind.ReceiptApplied;
        }

        /// <inheritdoc />
        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!CorruptedPreparedArtifacts &&
                checkpoint.Kind == SquadTransactionCheckpointKind.Prepared)
            {
                string transactionDirectory = Path.Combine(
                    root,
                    ".kyber-weave",
                    ".squad-transaction");
                string stagePath = ToPlatformPath(
                    Path.Combine(transactionDirectory, "staging"),
                    $"codex/{relativePath}");
                switch (corruption)
                {
                    case "missing":
                        files.DeleteFile(stagePath);
                        break;
                    case "mutated":
                        files.WriteAllText(
                            stagePath,
                            "mutated after prepared publication");
                        break;
                    case "extra":
                        string extraPath = Path.Combine(
                            transactionDirectory,
                            "unexpected",
                            "closed-set-extra.bin");
                        files.CreateDirectory(Path.GetDirectoryName(extraPath)!);
                        files.WriteAllText(extraPath, "undeclared prepared artifact");
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown prepared-boundary corruption '{corruption}'.");
                }

                CorruptedPreparedArtifacts = true;
                return;
            }

            if (!CorruptedPreparedArtifacts)
                return;

            SawLeafOrStateMutation |= checkpoint.Kind is
                SquadTransactionCheckpointKind.ActiveTransitionWritten or
                SquadTransactionCheckpointKind.OriginalClaimed or
                SquadTransactionCheckpointKind.AfterImagePublished;
        }
    }

    /// <summary>Changes the stage of one artifact once the transaction has claimed its original.</summary>
    internal sealed class OriginalClaimStageTamperingObserver(
        InMemoryFileSystem files,
        string root,
        string targetRelativePath,
        string subject,
        string mutation) : ISquadTransactionCheckpointObserver
    {
        private readonly string _checkpointSubject = string.Equals(
            subject,
            "target",
            StringComparison.Ordinal)
            ? targetRelativePath
            : subject;

        /// <summary>Gets whether the original claim was reached.</summary>
        public bool SawOriginalClaimed { get; private set; }

        /// <summary>Gets whether the affected after-image was published.</summary>
        public bool SawAffectedAfterImage { get; private set; }

        /// <summary>Gets whether the affected artifact was applied publicly.</summary>
        public bool SawAffectedPublicApply { get; private set; }

        /// <summary>Gets the mutated stage entry as the tree held it.</summary>
        public TreeEntry? MutatedStageEntry { get; private set; }

        /// <summary>Gets the journal's path.</summary>
        public string IntentPath => Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");

        /// <summary>Gets the '/'-separated path of the stage the observer mutates.</summary>
        public string StageRelativePath => subject switch
        {
            "target" => $".kyber-weave/.squad-transaction/staging/codex/{targetRelativePath}",
            "lock" => ".kyber-weave/.squad-transaction/state-staging/lock",
            "receipt" => ".kyber-weave/.squad-transaction/state-staging/receipt",
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedPublicApply |= subject switch
            {
                "target" => step.Kind == SquadTransactionStepKind.FileApplied &&
                            string.Equals(
                                step.RelativePath,
                                targetRelativePath,
                                StringComparison.Ordinal),
                "lock" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => throw new InvalidOperationException(
                    $"Unknown claim-stage subject '{subject}'.")
            };
        }

        /// <inheritdoc />
        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!string.Equals(
                    checkpoint.RelativePath,
                    _checkpointSubject,
                    StringComparison.Ordinal))
                return;

            SawAffectedAfterImage |=
                checkpoint.Kind == SquadTransactionCheckpointKind.AfterImagePublished;
            if (SawOriginalClaimed ||
                checkpoint.Kind != SquadTransactionCheckpointKind.OriginalClaimed)
                return;

            SawOriginalClaimed = true;
            if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
                return;

            string stagePath = ToPlatformPath(root, StageRelativePath);
            Assert.True(
                files.FileExists(stagePath),
                $"Prepared stage '{stagePath}' must exist at OriginalClaimed.");
            switch (mutation)
            {
                case "edit-bytes":
                    files.WriteAllText(
                        stagePath,
                        $"changed {subject} stage after claim");
                    break;
                case "replacement-link":
                    files.DeleteFile(stagePath);
                    files.CreateFileLink(stagePath, "replacement-stage-target");
                    break;
                case "replacement-directory":
                    files.DeleteFile(stagePath);
                    files.CreateDirectory(stagePath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown claim-stage mutation '{mutation}'.");
            }

            MutatedStageEntry = CaptureTree(files, root)[StageRelativePath];
        }
    }

    /// <summary>Changes the claimed original once the transaction has claimed it.</summary>
    internal sealed class OriginalClaimTamperingObserver(
        InMemoryFileSystem files,
        string root,
        string subject,
        string mutation) : ISquadTransactionCheckpointObserver
    {
        /// <summary>Gets whether the original claim was reached.</summary>
        public bool SawOriginalClaimed { get; private set; }

        /// <summary>Gets whether the affected after-image was published.</summary>
        public bool SawAffectedAfterImage { get; private set; }

        /// <summary>Gets whether the affected artifact was applied publicly.</summary>
        public bool SawAffectedPublicApply { get; private set; }

        /// <summary>Gets the '/'-separated path of the claim the observer mutates.</summary>
        public string ClaimRelativePath { get; private set; } = string.Empty;

        /// <summary>Gets the mutated claim entry as the tree held it.</summary>
        public TreeEntry? MutatedClaimEntry { get; private set; }

        /// <summary>Gets the tree captured after the mutation.</summary>
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        /// <summary>Gets the journal's path.</summary>
        public string IntentPath => Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedPublicApply |= subject switch
            {
                "lock" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => step.Kind == SquadTransactionStepKind.FileApplied &&
                     string.Equals(step.RelativePath, subject, StringComparison.Ordinal)
            };
        }

        /// <inheritdoc />
        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!string.Equals(checkpoint.RelativePath, subject, StringComparison.Ordinal))
                return;

            SawAffectedAfterImage |=
                checkpoint.Kind == SquadTransactionCheckpointKind.AfterImagePublished;
            if (checkpoint.Kind != SquadTransactionCheckpointKind.OriginalClaimed)
                return;

            SawOriginalClaimed = true;
            using JsonDocument intent = JsonDocument.Parse(files.ReadAllBytes(IntentPath));
            JsonElement activeClaim = intent.RootElement
                .GetProperty("artifacts")
                .EnumerateArray()
                .Single(artifact =>
                    string.Equals(
                        artifact.GetProperty("role").GetString(),
                        "claimed-original",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        artifact.GetProperty("lifecycleState").GetString(),
                        "active-transition",
                        StringComparison.Ordinal));
            string artifactRelativePath = Assert.IsType<string>(
                activeClaim.GetProperty("path").GetString());
            string claimPath = ToPlatformPath(
                Path.Combine(root, ".kyber-weave", ".squad-transaction"),
                artifactRelativePath);
            ClaimRelativePath = Path.GetRelativePath(root, claimPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            switch (mutation)
            {
                case "edit-bytes":
                    files.WriteAllText(
                        claimPath,
                        $"changed {subject} claim bytes");
                    break;
                case "retarget-file-link":
                    files.DeleteFile(claimPath);
                    files.CreateFileLink(claimPath, "retargeted-file-claim");
                    break;
                case "retarget-directory-link":
                    files.DeleteTreeNoFollow(claimPath);
                    files.CreateDirectoryLink(claimPath, "retargeted-directory-claim");
                    break;
                case "replace-directory":
                    files.DeleteFile(claimPath);
                    files.CreateDirectory(claimPath);
                    break;
                case "replace-file":
                    files.DeleteTreeNoFollow(claimPath);
                    files.WriteAllText(
                        claimPath,
                        $"replacement {subject} claim file");
                    break;
                case "unchanged":
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown claim mutation '{mutation}'.");
            }

            MutatedClaimEntry = CaptureTree(files, root)[ClaimRelativePath];
            InterruptedTreeSnapshot = CaptureTree(files, root);
            throw new InjectedSquadTransactionFailure(checkpoint.Sequence);
        }
    }

    /// <summary>Replaces the applied artifact with an operator's own bytes after the transaction applied it.</summary>
    internal sealed class PostIntentReplacementObserver(
        InMemoryFileSystem files,
        string root,
        SquadStateStore store,
        SquadDeploymentScope scope,
        string relativePath,
        SquadTransactionStepKind checkpoint) : ISquadTransactionObserver
    {
        /// <summary>Gets the bytes the operator wrote.</summary>
        public byte[] ExternalBytes { get; } = Encoding.UTF8.GetBytes(
            $"external replacement after {checkpoint}");

        /// <summary>Gets the path the operator replaced.</summary>
        public string ReplacedPath => checkpoint switch
        {
            SquadTransactionStepKind.FileApplied => ToPlatformPath(root, relativePath),
            SquadTransactionStepKind.LockApplied => store.ResolveLockPath(root, scope),
            SquadTransactionStepKind.ReceiptApplied => store.ResolveReceiptPath(root, scope),
            _ => throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint, null)
        };

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != checkpoint)
                return;

            files.CreateDirectory(Path.GetDirectoryName(ReplacedPath)!);
            files.WriteAllBytes(ReplacedPath, ExternalBytes);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    /// <summary>Writes an operator file beside the generated one inside a directory the transaction created.</summary>
    internal sealed class ExternalChildObserver(
        InMemoryFileSystem files,
        string root,
        string generatedPath,
        string externalPath) : ISquadTransactionObserver
    {
        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != SquadTransactionStepKind.FileApplied ||
                !string.Equals(step.RelativePath, generatedPath, StringComparison.Ordinal))
                return;

            string path = ToPlatformPath(root, externalPath);
            files.CreateDirectory(Path.GetDirectoryName(path)!);
            files.WriteAllText(path, "external operator note");
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    /// <summary>Stops the run after the publication of one named file and captures the tree.</summary>
    internal sealed class LastFileAppliedFailingObserver(
        InMemoryFileSystem files,
        string root,
        string relativePath) : ISquadTransactionObserver
    {
        /// <summary>Gets the tree captured at the interrupted boundary.</summary>
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != SquadTransactionStepKind.FileApplied ||
                !string.Equals(step.RelativePath, relativePath, StringComparison.Ordinal))
                return;

            InterruptedTreeSnapshot = CaptureTree(files, root);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    /// <summary>
    ///     Drops a file no part of this deployment owns into a staging directory once the first
    ///     target file has been applied, and records whether the staging tree kept it from there on.
    /// </summary>
    internal sealed class ForeignStageFileObserver(
        InMemoryFileSystem files,
        string root,
        string adoptedRelativePath,
        string pendingStageRelativePath) : ISquadTransactionObserver
    {
        private readonly string _stagingDirectory = Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "staging");

        /// <summary>Gets whether the foreign file was injected.</summary>
        public bool Injected { get; private set; }

        /// <summary>Gets whether another target's pending stage was still present after the injection.</summary>
        public bool SawPendingStageOfAnotherTarget { get; private set; }

        /// <summary>Gets whether the applied files' own branches were pruned while the blocked one was kept.</summary>
        public bool? SawAppliedFilePrunedOtherBranches { get; private set; }

        /// <summary>Gets what the cleanup removed that it should have kept.</summary>
        public List<string> Violations { get; } = [];

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            if (!Injected)
            {
                if (step.Kind != SquadTransactionStepKind.FileApplied)
                    return;

                files.CreateDirectory(Staging("codex"));
                files.WriteAllText(Staging("codex", "foreign.toml"), "foreign");
                Injected = true;
                return;
            }

            SawPendingStageOfAnotherTarget |= files.FileExists(Staging(pendingStageRelativePath));
            if (!files.FileExists(Staging("codex", "foreign.toml")) ||
                !files.DirectoryExists(Staging("codex")))
                Violations.Add($"the foreign stage file's directory was removed by {step.Kind}");

            string adoptedPath = ToPlatformPath(root, adoptedRelativePath);
            if (!files.FileExists(adoptedPath) ||
                !files.DirectoryExists(Path.GetDirectoryName(adoptedPath)!))
                Violations.Add($"the adopted file's directory was removed by {step.Kind}");

            if (step.Kind == SquadTransactionStepKind.LockApplied)
            {
                bool prunedAppliedBranches =
                    !files.DirectoryExists(Staging("codex", ".codex", "agents")) &&
                    !files.DirectoryExists(Staging("warp"));
                bool keptBlockedBranch =
                    files.DirectoryExists(Staging()) && files.DirectoryExists(Staging("codex"));
                SawAppliedFilePrunedOtherBranches = prunedAppliedBranches && keptBlockedBranch;
            }
        }

        private string Staging(params string[] relativeSegments)
        {
            string path = _stagingDirectory;
            foreach (string segment in relativeSegments)
                path = Path.Combine(path, segment);

            return path;
        }
    }

    /// <summary>Checks the prepared generation is complete before the first target mutation.</summary>
    internal sealed class PreparedArtifactsObserver(
        InMemoryFileSystem files,
        string root,
        string targetRelativePath) : ISquadTransactionCheckpointObserver
    {
        /// <summary>Gets whether the prepared checkpoint was observed.</summary>
        public bool ObservedPreparedCheckpoint { get; private set; }

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
        }

        /// <inheritdoc />
        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (checkpoint.Kind != SquadTransactionCheckpointKind.Prepared)
                return;

            Assert.Equal(
                "installed body",
                files.ReadAllText(ToPlatformPath(root, targetRelativePath)));
            string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
            string[] temporaryArtifacts = files
                .EnumerateFiles(transactionDirectory, "*", recursive: true)
                .Where(path => Path.GetFileName(path).Contains("tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(temporaryArtifacts);

            string intentPath = Path.Combine(transactionDirectory, "intent.json");
            using JsonDocument journal = JsonDocument.Parse(files.ReadAllBytes(intentPath));
            Assert.Equal("prepared", journal.RootElement.GetProperty("phase").GetString());
            JsonElement[] artifacts = journal.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.NotEmpty(artifacts);
            foreach (JsonElement artifact in artifacts)
            {
                string? relativeArtifactPath = artifact.GetProperty("path").GetString();
                Assert.False(string.IsNullOrWhiteSpace(relativeArtifactPath));
                string artifactPath = ToPlatformPath(
                    transactionDirectory,
                    Assert.IsType<string>(relativeArtifactPath));
                byte[] bytes = files.ReadAllBytes(artifactPath);
                Assert.Equal(bytes.LongLength, artifact.GetProperty("byteLength").GetInt64());
                Assert.Equal(Digest(bytes), artifact.GetProperty("sha256").GetString());
            }

            ObservedPreparedCheckpoint = true;
        }
    }

    /// <summary>
    ///     Records where the durable intent, the staged bytes and the backups live while a global
    ///     transaction runs. The intent, staging and backups all belong in the home journal, never beside the
    ///     target root and never in the portable global state directory.
    /// </summary>
    internal sealed class GlobalTransactionLocationObserver(
        InMemoryFileSystem files,
        string targetRoot,
        string stateDirectory,
        string journalDirectory) : ISquadTransactionObserver
    {
        /// <summary>Gets whether the durable intent was seen in the journal.</summary>
        public bool SawDurableIntentInJournal { get; private set; }

        /// <summary>Gets whether staging was seen in the journal.</summary>
        public bool SawStagingInJournal { get; private set; }

        /// <summary>Gets whether backups were seen in the journal.</summary>
        public bool SawBackupInJournal { get; private set; }

        /// <summary>Gets whether staging or backups were seen beside the target root.</summary>
        public bool SawStagingOrBackupOnTargetRoot { get; private set; }

        /// <summary>Gets whether staging or backups were seen in the portable state directory.</summary>
        public bool SawStagingOrBackupInState { get; private set; }

        /// <inheritdoc />
        public void AfterStep(SquadTransactionStep step)
        {
            SawDurableIntentInJournal |= files.DirectoryExists(journalDirectory) &&
                                         files.EnumerateFiles(
                                             journalDirectory,
                                             "intent.json",
                                             recursive: true).Any();
            if (step.Kind == SquadTransactionStepKind.FileStaged)
            {
                SawStagingInJournal |= HasDirectoryNamed(files, journalDirectory, "staging");
                SawStagingOrBackupOnTargetRoot |= HasDirectoryNamed(files, targetRoot, "staging");
                SawStagingOrBackupInState |= HasDirectoryNamed(files, stateDirectory, "staging");
            }

            if (step.Kind == SquadTransactionStepKind.FileBackedUp)
            {
                SawBackupInJournal |= HasDirectoryNamed(files, journalDirectory, "backups");
                SawStagingOrBackupOnTargetRoot |= HasDirectoryNamed(files, targetRoot, "backups");
                SawStagingOrBackupInState |= HasDirectoryNamed(files, stateDirectory, "backups");
            }
        }

        private static bool HasDirectoryNamed(
            InMemoryFileSystem files,
            string root,
            string name) =>
            files.DirectoryExists(root) &&
            files.EnumerateEntries(root).Any(entry =>
                entry.IsDirectory &&
                (string.Equals(Path.GetFileName(entry.Path), name, StringComparison.Ordinal) ||
                 (!entry.IsLink && HasDirectoryNamed(files, entry.Path, name))));
    }
}
