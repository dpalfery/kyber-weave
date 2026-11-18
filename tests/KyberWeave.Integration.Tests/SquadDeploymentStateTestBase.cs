using KyberWeave.Testing.Squad;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The helpers, nested types and fixtures shared by the deployment-state test classes. Each
/// class owns one theme of the deployment contract; every test builds its own state, so the
/// themes run on different cores without sharing anything between them.
/// </summary>
public abstract class SquadDeploymentStateTestBase
{
    internal const string GuidanceFile = SquadResponseGuidanceDeployer.FileName;

    internal static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    internal static readonly string[] ExpectedCodexOwner = ["codex"];

    /// <summary>
    ///     Reads the raw owning target tokens recorded for a degraded path from the
    ///     <see cref="SquadDegradedFile"/> contract, so a change to its shape fails at compile
    ///     time rather than returning empty owners.
    /// </summary>
    internal static IReadOnlyList<string> OwningTargetsOf(SquadDeploymentPlan plan, string relativePath) =>
        plan.DegradedFiles
            .Where(degraded => degraded.RelativePath == relativePath)
            .Select(degraded => degraded.Target)
            .ToList();

    internal static readonly SquadOwnedFile CodexOwned =
        new(".codex/agents/conductor.toml", Digest("codex body"), "codex", false);

    internal static readonly SquadOwnedFile CursorOwned =
        new(".cursor/agents/reviewer.md", Digest("cursor body"), "cursor", false);

    internal static readonly SquadDegradation[] CodexAndCursorDegradations =
    [
        new("codex", "conductor", "codex-code"),
        new("cursor", "reviewer", "cursor-code")
    ];

    /// <summary>
    ///     A symlink planted inside the home store that leads out of the home must stop every home-store
    ///     resolution it sits on. Planted at <c>.kyber-weave</c> it covers the state files and the global
    ///     journal, which lives beside <c>squad</c>. Planted at <c>.kyber-weave/squad</c> it covers the
    ///     state files only, so the journal is not asserted to reject there. Nothing here depends on
    ///     letter case, so it runs on every filesystem.
    /// </summary>
    internal static void AssertHomeStoreSymlinkEscapesAreRejected(string workspace)
    {
        const string canaryRelativePath = "outside-canary.txt";
        string project = Path.Combine(workspace, "home-escape-project");
        Directory.CreateDirectory(project);
        (string Label, string LinkRelativePath, bool JournalOnPath)[] plants =
        [
            ("kyber-weave", ".kyber-weave", true),
            ("squad", Path.Combine(".kyber-weave", "squad"), false)
        ];

        foreach ((string label, string linkRelativePath, bool journalOnPath) in plants)
        {
            string applicationData = Path.Combine(workspace, $"home-escape-{label}");
            string home = Path.Combine(applicationData, "home");
            string outside = Path.Combine(workspace, $"home-outside-{label}");
            Directory.CreateDirectory(Path.Combine(home, Path.GetDirectoryName(linkRelativePath)!));
            Directory.CreateDirectory(outside);
            Write(outside, canaryRelativePath, "outside canary");
            SortedDictionary<string, TreeEntry> outsideBefore = CaptureTree(outside);
            Directory.CreateSymbolicLink(Path.Combine(home, linkRelativePath), outside);

            SquadStateStore store = Store(applicationData);
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveReceiptPath(project, SquadDeploymentScope.Project));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveLockPath(project, SquadDeploymentScope.Project));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveReceiptPath(project, SquadDeploymentScope.Global));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveLockPath(project, SquadDeploymentScope.Global));
            if (journalOnPath)
                Assert.Throws<SquadPathContainmentException>(() =>
                    store.ResolveTransactionDirectory(project, SquadDeploymentScope.Global));

            AssertTreesEqual(outsideBefore, CaptureTree(outside));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Behavior (1): Receipt layout marker tests — v2 with layout: per-target-roots for global
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // Behavior (2): Receipt validation — v2 rejected when invalid, v1 mixed paths rejected
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // Behavior (3): Uninstall of legacy v1 global receipt with target-prefixed paths
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // Behavior (4): Update and install refuse legacy receipts before download
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // Behavior (5): v1 receipt with bare paths updates normally and rewrites as v2 per-target
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // Behavior (6): CreateUpdate stamps its output layout from what it actually retains, even
    // with no resolver supplied — closing the gap where a mismatched label could otherwise be
    // persisted and later trusted by a run that does supply one (F1).
    // ---------------------------------------------------------------------------------------

    internal static void AssertCaughtClaimMutation(
        string subject,
        string originalKind,
        string mutation)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        ClaimMutationScenario scenario = CreateClaimMutationScenario(
            fixturePath,
            subject,
            originalKind);
        SortedDictionary<string, TreeEntry> original = CaptureTree(fixturePath);
        OriginalClaimTamperingObserver observer = new(
            fixturePath,
            scenario.Subject,
            mutation);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(scenario.Plan));

        Assert.True(observer.SawOriginalClaimed);
        Assert.False(observer.SawAffectedAfterImage);
        Assert.False(observer.SawAffectedPublicApply);
        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Assert.IsType<InjectedSquadTransactionFailure>(exception);
            AssertTreesEqual(original, CaptureTree(fixturePath));
            Assert.False(Directory.Exists(Path.Combine(
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
        Assert.True(File.Exists(observer.IntentPath));

        SquadTransaction recoveryTransaction = Transaction(fixturePath);
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

    internal static void AssertRehydratedClaimMutation(
        string subject,
        string originalKind,
        string mutation)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        ClaimMutationScenario scenario = CreateClaimMutationScenario(
            fixturePath,
            subject,
            originalKind);
        SortedDictionary<string, TreeEntry> original = CaptureTree(fixturePath);
        OriginalClaimTamperingObserver observer = new(
            fixturePath,
            scenario.Subject,
            mutation);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        _ = Record.Exception(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Transaction(fixturePath).Recover(
                fixturePath,
                SquadDeploymentScope.Project);
            SortedDictionary<string, TreeEntry> controlAfterFirstRecovery = CaptureTree(fixturePath);
            Transaction(fixturePath).Recover(
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

        SquadTransaction recoveryTransaction = Transaction(fixturePath);
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

    internal static ClaimMutationScenario CreateClaimMutationScenario(
        string root,
        string subject,
        string originalKind)
    {
        const string targetRelativePath = ".codex/agents/conductor.toml";
        const string installedBody = "installed claim body";
        Write(root, targetRelativePath, installedBody);
        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            targetRelativePath,
            Digest(installedBody),
            "codex",
            false));
        WriteState(root, Lock(), previousReceipt);
        SquadStateStore store = Store(root);
        string livePath = subject switch
        {
            "target" => ToPlatformPath(root, targetRelativePath),
            "lock" => store.ResolveLockPath(root, SquadDeploymentScope.Project),
            "receipt" => store.ResolveReceiptPath(root, SquadDeploymentScope.Project),
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };
        ConfigureOriginalNode(root, livePath, subject, originalKind);
        string renderedBody = string.Equals(subject, "target", StringComparison.Ordinal)
            ? "updated claim body"
            : installedBody;
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(targetRelativePath, renderedBody)],
            previousReceipt,
            [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        return new ClaimMutationScenario(
            plan,
            string.Equals(subject, "target", StringComparison.Ordinal)
                ? targetRelativePath
                : subject,
            Path.GetRelativePath(root, livePath).Replace(Path.DirectorySeparatorChar, '/'));
    }

    internal static AfterImageRestoreScenario CreateAfterImageRestoreScenario(
        string root,
        string artifactRole)
    {
        (string subject, string originalKind) = artifactRole switch
        {
            "target-backup" => ("target", "file"),
            "target-link-metadata" => ("target", "file-link"),
            "lock-original" => ("lock", "file"),
            "lock-link-metadata" => ("lock", "file-link"),
            "receipt-original" => ("receipt", "file"),
            "receipt-link-metadata" => ("receipt", "file-link"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(artifactRole), artifactRole, null)
        };
        ClaimMutationScenario scenario = CreateClaimMutationScenario(root, subject, originalKind);
        return new AfterImageRestoreScenario(scenario.Plan, scenario.Subject);
    }

    internal static void ConfigureOriginalNode(
        string root,
        string livePath,
        string subject,
        string originalKind)
    {
        if (string.Equals(originalKind, "file", StringComparison.Ordinal))
            return;

        DeleteLeafEntry(livePath);
        string fixtureDirectory = string.Equals(subject, "target", StringComparison.Ordinal)
            ? Path.Combine(root, ".claim-fixtures")
            : Path.Combine(Path.GetDirectoryName(livePath)!, ".claim-fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        switch (originalKind)
        {
            case "file-link":
                {
                    string linkTargetPath = Path.Combine(fixtureDirectory, $"{subject}-file.txt");
                    string content = string.Equals(subject, "target", StringComparison.Ordinal)
                        ? "installed claim body"
                        : $"original {subject} state";
                    File.WriteAllText(linkTargetPath, content, new UTF8Encoding(false));
                    Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                    File.CreateSymbolicLink(
                        livePath,
                        Path.GetRelativePath(Path.GetDirectoryName(livePath)!, linkTargetPath));
                    break;
                }
            case "directory-link":
                {
                    string linkTargetPath = Path.Combine(fixtureDirectory, $"{subject}-directory");
                    Directory.CreateDirectory(linkTargetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                    Directory.CreateSymbolicLink(
                        livePath,
                        Path.GetRelativePath(Path.GetDirectoryName(livePath)!, linkTargetPath));
                    break;
                }
            case "directory":
                Directory.CreateDirectory(livePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(originalKind), originalKind, null);
        }
    }

    internal static void DeleteLeafEntry(string path)
    {
        DirectoryInfo directory = new(path);
        if (directory.LinkTarget is not null)
        {
            Directory.Delete(path);
            return;
        }

        FileInfo file = new(path);
        if (file.LinkTarget is not null || file.Exists)
        {
            File.Delete(path);
            return;
        }

        if (directory.Exists)
            Directory.Delete(path, true);
    }

    internal static string ExtractMethodDeclaration(string source, string methodName)
    {
        int searchFrom = 0;
        while (true)
        {
            int methodNameIndex = source.IndexOf(
                $"{methodName}(",
                searchFrom,
                StringComparison.Ordinal);
            Assert.True(methodNameIndex >= 0, $"Method '{methodName}' was not found.");
            int declarationStart = source.LastIndexOf(
                "    private ",
                methodNameIndex,
                StringComparison.Ordinal);
            int bodyStart = source.IndexOf('{', methodNameIndex);
            Assert.True(declarationStart >= 0 && bodyStart > methodNameIndex);
            string declaration = source[declarationStart..bodyStart];
            if (declaration.Contains(methodName, StringComparison.Ordinal))
                return declaration;

            searchFrom = methodNameIndex + methodName.Length;
        }
    }

    internal static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    internal static RestoreArtifactScenario CreateRestoreArtifactScenario(
        string root,
        string artifactRole)
    {
        const string targetRelativePath = ".codex/agents/conductor.toml";
        const string installedBody = "installed body";
        string targetPath = ToPlatformPath(root, targetRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (string.Equals(artifactRole, "target-link-metadata", StringComparison.Ordinal))
        {
            const string canonicalRelativePath = "canonical/conductor.toml";
            string canonicalPath = Path.Combine(root, "canonical", "conductor.toml");
            Write(root, canonicalRelativePath, installedBody);
            File.CreateSymbolicLink(
                targetPath,
                Path.GetRelativePath(Path.GetDirectoryName(targetPath)!, canonicalPath));
        }
        else
        {
            File.WriteAllText(targetPath, installedBody, new UTF8Encoding(false));
        }

        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            targetRelativePath,
            Digest(installedBody),
            "codex",
            false));
        WriteState(root, Lock(), previousReceipt);
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(targetRelativePath, "updated body")],
            previousReceipt,
            [new SquadDegradation("codex", "conductor", "model-profile-narrowed")],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        string transactionDirectory = Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction");
        SquadStateStore store = Store(root);
        (SquadTransactionStepKind boundary, string artifactPath, string liveRelativePath, byte[] expectedBytes) =
            artifactRole switch
            {
                "target-file" => (
                    SquadTransactionStepKind.FileApplied,
                    ToPlatformPath(
                        Path.Combine(transactionDirectory, "backups"),
                        $"codex/{targetRelativePath}"),
                    targetRelativePath,
                    "updated body"u8.ToArray()),
                "target-link-metadata" => (
                    SquadTransactionStepKind.FileApplied,
                    ToPlatformPath(
                        Path.Combine(transactionDirectory, "links"),
                        $"codex/{targetRelativePath}"),
                    targetRelativePath,
                    "updated body"u8.ToArray()),
                "lock-original" => (
                    SquadTransactionStepKind.LockApplied,
                    Path.Combine(transactionDirectory, "state-originals", "lock"),
                    StateRelativePath(root, store.ResolveLockPath(root, SquadDeploymentScope.Project)),
                    Encoding.UTF8.GetBytes(store.SerializeLock(
                        plan.Lock ?? throw new InvalidOperationException("The update plan has no lock.")))),
                "receipt-original" => (
                    SquadTransactionStepKind.ReceiptApplied,
                    Path.Combine(transactionDirectory, "state-originals", "receipt"),
                    StateRelativePath(root, store.ResolveReceiptPath(root, SquadDeploymentScope.Project)),
                    Encoding.UTF8.GetBytes(store.SerializeReceipt(plan.Receipt))),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(artifactRole), artifactRole, "Unknown restore artifact role.")
            };
        return new RestoreArtifactScenario(
            plan,
            boundary,
            artifactPath,
            Path.Combine(transactionDirectory, "intent.json"),
            liveRelativePath,
            new TreeEntry(TreeEntryKind.File, expectedBytes, null));
    }

    internal static string ExtractMethod(string source, string methodName)
    {
        int searchFrom = 0;
        while (true)
        {
            int methodNameIndex = source.IndexOf(
                $"{methodName}(",
                searchFrom,
                StringComparison.Ordinal);
            Assert.True(methodNameIndex >= 0, $"Method '{methodName}' was not found.");
            int lineStart = source.LastIndexOf('\n', methodNameIndex) + 1;
            string declarationPrefix = source[lineStart..methodNameIndex];
            if (declarationPrefix.Contains("private", StringComparison.Ordinal))
            {
                int bodyStart = source.IndexOf('{', methodNameIndex);
                Assert.True(bodyStart >= 0);
                int depth = 0;
                for (int index = bodyStart; index < source.Length; index++)
                {
                    depth += source[index] switch
                    {
                        '{' => 1,
                        '}' => -1,
                        _ => 0
                    };
                    if (depth == 0)
                        return source[bodyStart..(index + 1)];
                }

                throw new XunitException(
                    $"Method '{methodName}' has no complete body.");
            }

            searchFrom = methodNameIndex + methodName.Length;
        }
    }

    internal static SortedDictionary<string, TreeEntry> CaptureTreeWithoutTransactionEvidence(
        string root)
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

    internal static void EditSemanticArtifactSet(
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
            DeleteArtifactNode(ToPlatformPath(transactionDirectory, removedRelativePath));
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
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
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

    internal static void DeleteArtifactNode(string path)
    {
        FileInfo file = new(path);
        if (file.LinkTarget is not null || file.Exists)
        {
            file.Delete();
            return;
        }

        DirectoryInfo directory = new(path);
        if (directory.LinkTarget is not null || directory.Exists)
            directory.Delete(false);
    }

    internal static void RemoveEmptyTestArtifactParents(string artifactPath, string root)
    {
        string? current = Path.GetDirectoryName(artifactPath);
        while (current is not null &&
               !string.Equals(current, root, StringComparison.Ordinal) &&
               Directory.Exists(current) &&
               !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    internal static ActiveTransitionScenario CreateActiveTransitionScenario(
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
                RealSquadDeployment.Planner.CreateUpdate(
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
            Directory.CreateDirectory(Path.GetDirectoryName(fullLinkPath)!);
            File.CreateSymbolicLink(
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
                RealSquadDeployment.Planner.CreateUpdate(
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
                RealSquadDeployment.Planner.CreateUpdate(
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
                RealSquadDeployment.Planner.CreateUninstall(
                    root,
                    SquadDeploymentScope.Project,
                    emptyReceipt),
                scenario.StartsWith("lock", StringComparison.Ordinal) ? "lock" : "receipt");

        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
    }

    internal static bool TryEnableWindowsDirectoryCaseSensitivity(
        string directory,
        out string diagnostic)
    {
        if (!OperatingSystem.IsWindows())
        {
            diagnostic = "host is not Windows";
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new("fsutil.exe")
            {
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("file");
            startInfo.ArgumentList.Add("setCaseSensitiveInfo");
            startInfo.ArgumentList.Add(directory);
            startInfo.ArgumentList.Add("enable");

            // Bounded: ProcessRunner kills the tree if fsutil has not exited in 60 s. A start failure throws
            // InvalidOperationException, which the catch below turns into the diagnostic.
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
            diagnostic = string.Join(
                " ",
                new[] { result.StandardOutput, result.StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()));
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    internal static SquadLock ReplaceRequiredLockValue(
        SquadLock value,
        string field,
        string replacement)
    {
        return field switch
        {
            "schema" => value with { Schema = replacement },
            "squad-version" => value with { SquadVersion = replacement },
            "cli-version" => value with { CliVersion = replacement },
            "mcp-version" => value with { McpVersion = replacement },
            "bundle" => value with { Bundle = replacement },
            "translation" => value with { Translation = replacement },
            "apm-version" => value with { Apm = value.Apm with { Version = replacement } },
            "apm-tag-commit" => value with { Apm = value.Apm with { TagCommit = replacement } },
            "target" => value with { Targets = [replacement] },
            "exclusion" => value with { Exclusions = [replacement] },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    internal static IEnumerable<(string Property, string Value, int Occurrence)>
        EnumerateAuthorityEnumTokens(JsonNode root)
    {
        HashSet<string> enumProperties = new(StringComparer.Ordinal)
        {
            "phase", "originalKind", "afterKind", "lockKind", "lockAfterKind",
            "receiptKind", "receiptAfterKind", "area", "role", "nodeKind",
            "lifecycleState", "allowedState"
        };
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        return Enumerate(root);

        IEnumerable<(string Property, string Value, int Occurrence)> Enumerate(JsonNode node)
        {
            if (node is JsonObject jsonObject)
                foreach ((string? property, JsonNode? child) in jsonObject)
                {
                    if (child is JsonValue value &&
                        enumProperties.Contains(property) &&
                        value.GetValueKind() == JsonValueKind.String)
                    {
                        string token = value.GetValue<string>();
                        string key = $"{property}\0{token}";
                        occurrences.TryGetValue(key, out int occurrence);
                        occurrences[key] = occurrence + 1;
                        yield return (property, token, occurrence);
                    }

                    if (child is not null)
                        foreach ((string Property, string Value, int Occurrence) nested in Enumerate(child))
                            yield return nested;
                }
            else if (node is JsonArray jsonArray)
                foreach (JsonNode? child in jsonArray)
                {
                    if (child is null)
                        continue;
                    foreach ((string Property, string Value, int Occurrence) nested in Enumerate(child))
                        yield return nested;
                }
        }
    }

    internal static bool ReplaceAuthorityEnumOccurrence(
        JsonNode root,
        string propertyName,
        string canonicalValue,
        int targetOccurrence,
        string replacement)
    {
        int occurrence = 0;
        return Replace(root);

        bool Replace(JsonNode node)
        {
            if (node is JsonObject jsonObject)
                foreach ((string? property, JsonNode? child) in jsonObject.ToArray())
                {
                    if (string.Equals(property, propertyName, StringComparison.Ordinal) &&
                        child is JsonValue value &&
                        value.GetValueKind() == JsonValueKind.String &&
                        string.Equals(
                            value.GetValue<string>(),
                            canonicalValue,
                            StringComparison.Ordinal))
                    {
                        if (occurrence == targetOccurrence)
                        {
                            jsonObject[property] = replacement;
                            return true;
                        }

                        occurrence++;
                    }

                    if (child is not null && Replace(child))
                        return true;
                }
            else if (node is JsonArray jsonArray)
                foreach (JsonNode? child in jsonArray)
                    if (child is not null && Replace(child))
                        return true;

            return false;
        }
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Case variance is the subject under test. The lowercase variant is "
                        + "one of the inputs being asserted on, not a normalization to be made safe.")]
    internal static IReadOnlyList<string> NoncanonicalCaseVariants(string canonical)
    {
        return new[]
            {
                canonical.ToLowerInvariant(),
                canonical.ToUpperInvariant(),
                char.ToUpperInvariant(canonical[0]) + canonical[1..],
                ToggleAsciiCase(canonical)
            }
            .Where(value => !string.Equals(value, canonical, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static JsonObject ReadJsonObject(string path)
    {
        return Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)));
    }

    internal static void WriteJsonObject(string path, JsonObject document)
    {
        File.WriteAllText(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
    }

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

    internal static void CorruptPreparedAuthority(
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
                File.CreateSymbolicLink(
                    Path.Combine(transactionDirectory, "undeclared-link"),
                    "missing-link-target");
                break;
            case "undeclared-directory":
                // An empty undescribed folder is what a kill leaves and recovery removes; one holding a file is not.
                Directory.CreateDirectory(Path.Combine(transactionDirectory, "undeclared-directory"));
                File.WriteAllText(
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
                File.WriteAllBytes(Path.Combine(transactionDirectory, unexpectedPath), unexpectedBytes);
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

    internal static string ReplaceJsonStringPropertyWithNumber(
        string json,
        string propertyName,
        int numericValue,
        out int replacementCount)
    {
        JsonNode root = JsonNode.Parse(json)
                        ?? throw new XunitException("Expected a JSON authority document.");
        replacementCount = ReplaceJsonStringPropertyWithNumber(
            root,
            propertyName,
            numericValue);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    internal static int ReplaceJsonStringPropertyWithNumber(
        JsonNode node,
        string propertyName,
        int numericValue)
    {
        int replacements = 0;
        if (node is JsonObject jsonObject)
            foreach ((string? name, JsonNode? value) in jsonObject.ToArray())
            {
                if (string.Equals(name, propertyName, StringComparison.Ordinal) &&
                    value is JsonValue &&
                    value.GetValueKind() == JsonValueKind.String)
                {
                    jsonObject[name] = numericValue;
                    replacements++;
                    continue;
                }

                if (value is not null)
                    replacements += ReplaceJsonStringPropertyWithNumber(
                        value,
                        propertyName,
                        numericValue);
            }
        else if (node is JsonArray jsonArray)
            foreach (JsonNode? child in jsonArray)
                if (child is not null)
                    replacements += ReplaceJsonStringPropertyWithNumber(
                        child,
                        propertyName,
                        numericValue);

        return replacements;
    }

    internal static void AssertJsonEnumTokensAreStrings(JsonNode node)
    {
        HashSet<string> enumProperties = new(StringComparer.Ordinal)
        {
            "phase", "originalKind", "afterKind", "lockKind", "lockAfterKind",
            "receiptKind", "receiptAfterKind", "area", "role", "nodeKind",
            "lifecycleState"
        };
        AssertJsonEnumTokensAreStrings(node, enumProperties);
    }

    internal static void AssertJsonEnumTokensAreStrings(
        JsonNode node,
        IReadOnlySet<string> enumProperties)
    {
        if (node is JsonObject jsonObject)
            foreach ((string? name, JsonNode? value) in jsonObject)
            {
                if (enumProperties.Contains(name))
                {
                    JsonValue token = Assert.IsAssignableFrom<JsonValue>(value);
                    Assert.Equal(JsonValueKind.String, token.GetValueKind());
                }

                if (value is not null)
                    AssertJsonEnumTokensAreStrings(value, enumProperties);
            }
        else if (node is JsonArray jsonArray)
            foreach (JsonNode? child in jsonArray)
                if (child is not null)
                    AssertJsonEnumTokensAreStrings(child, enumProperties);
    }

    internal static bool TreesEqual(
        IReadOnlyDictionary<string, TreeEntry> expected,
        IReadOnlyDictionary<string, TreeEntry> actual)
    {
        if (!expected.Keys.SequenceEqual(actual.Keys, StringComparer.Ordinal))
            return false;

        foreach ((string? path, TreeEntry? expectedEntry) in expected)
        {
            TreeEntry actualEntry = actual[path];
            if (expectedEntry.Kind != actualEntry.Kind ||
                !string.Equals(expectedEntry.LinkTarget, actualEntry.LinkTarget, StringComparison.Ordinal))
                return false;

            if (expectedEntry.Content is null != actualEntry.Content is null)
                return false;
            if (expectedEntry.Content is not null &&
                !expectedEntry.Content.SequenceEqual(Assert.IsType<byte[]>(actualEntry.Content)))
                return false;
        }

        return true;
    }

    internal static void AssertTreeEntryEqual(TreeEntry expected, TreeEntry actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.LinkTarget, actual.LinkTarget);
        if (expected.Content is null)
        {
            Assert.Null(actual.Content);
            return;
        }

        Assert.NotNull(actual.Content);
        Assert.True(
            expected.Content.SequenceEqual(actual.Content),
            "The raced leaf bytes changed during claim/publication.");
    }

    internal static string ToggleAsciiCase(string value)
    {
        return new string(value.Select(character =>
            char.IsAsciiLetterUpper(character)
                ? char.ToLowerInvariant(character)
                : char.IsAsciiLetterLower(character)
                    ? char.ToUpperInvariant(character)
                    : character).ToArray());
    }

    internal static SortedDictionary<string, TreeEntry> InterruptTransactionAfter(
        TransactionFixture fixture,
        SquadTransactionStepKind checkpoint)
    {
        LifecycleStepFailingObserver observer = new(fixture.Path, checkpoint);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan()));

        return Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
    }

    internal static SortedDictionary<string, TreeEntry> InterruptTransactionAfterCheckpoint(
        TransactionFixture fixture,
        SquadTransactionCheckpointKind checkpoint)
    {
        CheckpointFailingObserver observer = new(fixture.Path, checkpoint);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan()));

        return Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
    }

    internal static void CorruptPreparedArtifact(
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
                byte[] journal = File.ReadAllBytes(intentPath);
                File.WriteAllBytes(intentPath, journal[..Math.Max(1, journal.Length / 2)]);
                break;
            case "duplicate-journal-field":
                File.WriteAllText(
                    intentPath,
                    CorruptJournalDocument(
                        File.ReadAllText(intentPath, Encoding.UTF8),
                        "duplicate"),
                    new UTF8Encoding(false));
                break;
            case "partial-backup":
                byte[] backup = File.ReadAllBytes(backupPath);
                File.WriteAllBytes(backupPath, backup[..Math.Max(1, backup.Length / 2)]);
                break;
            case "wrong-backup-digest":
                byte[] wrong = File.ReadAllBytes(backupPath);
                wrong[0] ^= 0xff;
                File.WriteAllBytes(backupPath, wrong);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    internal static void ReplacePublishedArtifactWithPartialTemporaryFile(
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
        byte[] bytes = File.ReadAllBytes(publishedPath);
        string temporaryPath = publishedPath + ".tmp";
        File.Move(publishedPath, temporaryPath);
        File.WriteAllBytes(temporaryPath, bytes[..Math.Max(1, bytes.Length / 2)]);
    }

    internal static SquadDeploymentPlan CreateGlobalInstallPlan(string targetRoot)
    {
        return RealSquadDeployment.Planner.CreateInstall(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "global conductor")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
    }

    internal static int ProbeGlobalInstallStepCount()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        Directory.CreateDirectory(targetRoot);
        RecordingObserver observer = new(targetRoot);
        RealSquadDeployment.Transaction(
            Store(Path.Combine(fixture.Path, "application-data")),
            observer).Execute(CreateGlobalInstallPlan(targetRoot));
        return observer.Steps.Count;
    }

    internal static void AssertActiveLeaseConflict(Exception? exception)
    {
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CorruptLockDocument(string yaml, string corruption)
    {
        return corruption switch
        {
            "unknown" => yaml + "unknown-field: true\n",
            "duplicate" => yaml + "schema: kyber-squad.lock/v1\n",
            "missing" => yaml.Replace(
                "schema: kyber-squad.lock/v1\n",
                string.Empty,
                StringComparison.Ordinal),
            "null" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: null",
                StringComparison.Ordinal),
            "misspelled" => yaml.Replace("schema:", "schemma:", StringComparison.Ordinal),
            "wrong-case" => yaml.Replace("schema:", "Schema:", StringComparison.Ordinal),
            "wrong-type" => yaml.Replace(
                "targets:\n  - codex\n  - warp",
                "targets: 42",
                StringComparison.Ordinal),
            "yaml-alias" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: &schema kyber-squad.lock/v1",
                StringComparison.Ordinal),
            "yaml-merge" => "defaults: &defaults\n  schema: kyber-squad.lock/v1\n<<: *defaults\n" +
                            yaml.Replace(
                                "schema: kyber-squad.lock/v1\n",
                                string.Empty,
                                StringComparison.Ordinal),
            "yaml-tag" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: !!str kyber-squad.lock/v1",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };
    }

    internal static string CorruptReceiptDocument(string json, string corruption)
    {
        return corruption switch
        {
            "unknown" => InsertBeforeRootClose(json, "  \"unknownField\": true"),
            "duplicate" => InsertBeforeRootClose(
                json,
                "  \"schema\": \"kyber-squad.receipt/v1\""),
            "missing" => json
                .Replace("  \"schema\": \"kyber-squad.receipt/v1\",\r\n", string.Empty, StringComparison.Ordinal)
                .Replace("  \"schema\": \"kyber-squad.receipt/v1\",\n", string.Empty, StringComparison.Ordinal),
            "null" => json.Replace(
                "\"schema\": \"kyber-squad.receipt/v1\"",
                "\"schema\": null",
                StringComparison.Ordinal),
            "misspelled" => json.Replace("\"schema\":", "\"schemma\":", StringComparison.Ordinal),
            "wrong-case" => json.Replace("\"schema\":", "\"Schema\":", StringComparison.Ordinal),
            "wrong-type" => json.Replace("\"files\": [", "\"files\": 42, \"ignoredFiles\": [",
                StringComparison.Ordinal),
            "wrong-enum" => json.Replace("\"scope\": \"project\"", "\"scope\": \"workspace\"",
                StringComparison.Ordinal),
            "invalid-timestamp" => json.Replace(
                "2026-08-14T12:34:56.0000000Z",
                "not-a-timestamp",
                StringComparison.Ordinal),
            "invalid-digest" => json.Replace(Digest("conductor"), "not-a-digest", StringComparison.Ordinal),
            "portable-duplicate" => InsertDuplicateReceiptFile(json),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };
    }

    internal static string CorruptJournalDocument(string json, string corruption)
    {
        return corruption switch
        {
            "unknown" => InsertBeforeRootClose(json, "  \"unknownField\": true"),
            "duplicate" => InsertBeforeRootClose(
                json,
                "  \"schema\": \"kyber-squad.transaction/v1\""),
            "missing" => json
                .Replace("  \"schema\": \"kyber-squad.transaction/v1\",\r\n", string.Empty, StringComparison.Ordinal)
                .Replace("  \"schema\": \"kyber-squad.transaction/v1\",\n", string.Empty, StringComparison.Ordinal),
            "null" => json.Replace(
                "\"schema\": \"kyber-squad.transaction/v1\"",
                "\"schema\": null",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };
    }

    internal static string InsertBeforeRootClose(string json, string property)
    {
        int rootClose = json.LastIndexOf('}');
        Assert.True(rootClose >= 0);
        string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return json.Insert(rootClose, $",{newline}{property}{newline}");
    }

    internal static string InsertDuplicateReceiptFile(string json)
    {
        int insertion = json.LastIndexOf("  ]", StringComparison.Ordinal);
        Assert.True(insertion >= 0);
        string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string duplicate =
            $",{newline}    {{{newline}      \"relativePath\": \".codex/agents/Conductor.toml\",{newline}      \"sha256\": \"{Digest("conductor")}\",{newline}      \"target\": \"codex\",{newline}      \"adopted\": false{newline}    }}";
        return json.Insert(insertion + 3, duplicate);
    }

    internal static SquadHomeFile GuidanceHomeFile()
    {
        return new SquadHomeFile(GuidanceFile, Digest("guidance"));
    }

    internal static SquadReceipt GlobalReceiptWithHomeFiles(params SquadHomeFile[] homeFiles)
    {
        return new SquadReceipt(
            SquadStateSchemas.ReceiptSchemaV2,
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [])
        {
            HomeFiles = homeFiles
        };
    }

    internal static string HomeFileJson(string relativePath, string sha256)
    {
        return $$"""{"relativePath":"{{relativePath}}","sha256":"{{sha256}}"}""";
    }

    /// <summary>
    ///     A receipt that breaks exactly one rule of the home-file record, built from a valid global receipt
    ///     so the only thing wrong with it is the thing the case names.
    /// </summary>
    internal static SquadReceipt ReceiptWithInvalidHomeFiles(string invalidCase)
    {
        SquadHomeFile valid = GuidanceHomeFile();
        SquadReceipt globalReceipt = GlobalReceiptWithHomeFiles(valid);
        return invalidCase switch
        {
            "project-scope" => Receipt() with { HomeFiles = [valid] },
            "instance-scope" => Receipt() with { Scope = SquadDeploymentScope.Instance, HomeFiles = [valid] },
            "null-list" => globalReceipt with { HomeFiles = null! },
            "null-entry" => globalReceipt with { HomeFiles = [null!] },
            "parent-traversal" => globalReceipt with
            {
                HomeFiles = [valid with { RelativePath = "../response-guidance.md" }]
            },
            "rooted-path" => globalReceipt with
            {
                HomeFiles = [valid with { RelativePath = "/etc/response-guidance.md" }]
            },
            "reserved-device-name" => globalReceipt with
            {
                HomeFiles = [valid with { RelativePath = "con.md" }]
            },
            "backslash-separator" => globalReceipt with
            {
                HomeFiles = [valid with { RelativePath = "guidance\\response-guidance.md" }]
            },
            "short-digest" => globalReceipt with { HomeFiles = [valid with { Sha256 = "abc123" }] },
            "uppercase-digest" => globalReceipt with
            {
                HomeFiles = [valid with { Sha256 = valid.Sha256.ToUpperInvariant() }]
            },
            "non-hex-digest" => globalReceipt with
            {
                HomeFiles = [valid with { Sha256 = new string('g', 64) }]
            },
            "duplicate-path" => globalReceipt with
            {
                HomeFiles = [valid, valid with { Sha256 = Digest("other") }]
            },
            "case-variant-duplicate" => globalReceipt with
            {
                HomeFiles = [valid, valid with { RelativePath = "Response-Guidance.md" }]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null)
        };
    }

    /// <summary>
    ///     Swaps the value of a serialized global receipt's <c>homeFiles</c> property for raw JSON. The store
    ///     writes it as the last property, so everything from its name to the root's closing brace is its value.
    /// </summary>
    internal static string ReplaceHomeFiles(string json, string homeFilesJson)
    {
        int start = json.IndexOf("\"homeFiles\":", StringComparison.Ordinal);
        int rootClose = json.LastIndexOf('}');
        Assert.True(start >= 0 && rootClose > start, "The fixture receipt must end with its homeFiles property.");
        return $"{json[..start]}\"homeFiles\": {homeFilesJson}\n{json[rootClose..]}";
    }

    internal static SquadStateStore Store(string applicationData)
    {
        return RealSquadState.Store(new FakeSquadUserPaths(applicationData));
    }

    internal static SquadTransaction Transaction(
        string applicationData,
        ISquadTransactionObserver? observer = null)
    {
        return RealSquadDeployment.Transaction(Store(applicationData), observer);
    }

    internal static bool IsHeldWithExclusiveFileShare(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    internal static bool HasDirectoryNamed(string root, string name)
    {
        return Directory.Exists(root) && Directory.EnumerateDirectories(
                root,
                "*",
                SearchOption.AllDirectories)
            .Any(path => string.Equals(
                Path.GetFileName(path),
                name,
                StringComparison.Ordinal));
    }

    internal static SquadLock Lock(string version = "1.2.3")
    {
        return new SquadLock(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["codex", "warp"],
            ["cursor"],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));
    }

    internal static SquadReceipt Receipt(params SquadOwnedFile[] files)
    {
        return new SquadReceipt(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            files);
    }

    internal static SquadDeploymentFile Rendered(
        string relativePath,
        string content,
        string target = "codex")
    {
        return new SquadDeploymentFile(relativePath, Encoding.UTF8.GetBytes(content), target);
    }

    internal static string Digest(string content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>
    ///     Serializes <paramref name="receipt" /> as authentic legacy v1 JSON — the shape a genuine
    ///     pre-#91 CLI wrote, with no explicit <c>layout</c> field — rather than through
    ///     <see cref="SquadStateStore.SerializeReceipt" />, which always upgrades a Global receipt
    ///     to v2 regardless of the schema string already on the in-memory object. A directly
    ///     constructed receipt with prefixed paths but an unset (default)
    ///     <see cref="SquadReceipt.Layout" /> would otherwise round-trip through the v2 writer as a
    ///     self-contradicting payload — exactly what the v2 read-side cross-check (#91) now refuses.
    /// </summary>
    internal static string SerializeAsLegacyV1Json(SquadReceipt receipt)
    {
        JsonObject json = new()
        {
            ["schema"] = "kyber-squad.receipt/v1",
            ["scope"] = receipt.Scope == SquadDeploymentScope.Global ? "global" : "project",
            ["targetRoot"] = receipt.TargetRoot,
            ["installedAtUtc"] = receipt.InstalledAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["degradations"] = new JsonArray(
            [
                .. receipt.Degradations.Select(degradation => (JsonNode)new JsonObject
                {
                    ["target"] = degradation.Target,
                    ["subject"] = degradation.Subject,
                    ["code"] = degradation.Code
                })
            ]),
            ["files"] = new JsonArray(
            [
                .. receipt.Files.Select(file => (JsonNode)new JsonObject
                {
                    ["relativePath"] = file.RelativePath,
                    ["sha256"] = file.Sha256,
                    ["target"] = file.Target,
                    ["adopted"] = file.Adopted
                })
            ])
        };

        return json.ToJsonString() + "\n";
    }

    internal static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    internal static string Read(string root, string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    ///     Writes a project's committed lock and receipt where the store resolves them, so a fixture
    ///     stays correct when the state layout moves rather than pinning the layout it was born with.
    /// </summary>
    internal static void WriteState(string root, SquadLock squadLock, SquadReceipt receipt)
    {
        SquadStateStore store = Store(root);
        string lockPath = store.ResolveLockPath(root, SquadDeploymentScope.Project);
        string receiptPath = store.ResolveReceiptPath(root, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, store.SerializeLock(squadLock), new UTF8Encoding(false));
        File.WriteAllText(receiptPath, store.SerializeReceipt(receipt), new UTF8Encoding(false));
    }

    /// <summary>
    ///     Expresses a store-resolved state file as the '/'-separated key <see cref="CaptureTree" /> uses,
    ///     measured from the physical root. Both sides are canonical so a symlinked temp root still matches.
    /// </summary>
    internal static string StateRelativePath(string root, string statePath)
    {
        return Path.GetRelativePath(SquadPhysicalRootIdentities.Resolve(root, SquadFileSystemPathSemantics.Instance).PhysicalPath, statePath)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    ///     Compares two locations by where they physically are. A path may legitimately be spelled through
    ///     a symlinked temp root (<c>/tmp</c> resolving to <c>/private/tmp</c>) and still name one place, so
    ///     the comparison is on the canonical form rather than the spelling.
    /// </summary>
    internal static void AssertSameLocation(string expected, string actual)
    {
        Assert.Equal(CanonicalLocation(expected), CanonicalLocation(actual));
    }

    internal static string CanonicalLocation(string path)
    {
        string existing = Path.GetFullPath(path);
        List<string> unresolved = [];
        while (!Directory.Exists(existing))
        {
            unresolved.Insert(0, Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing)!;
        }

        return unresolved.Aggregate(
            SquadPhysicalRootIdentities.Resolve(existing, SquadFileSystemPathSemantics.Instance).PhysicalPath,
            (accumulated, part) => Path.Combine(accumulated, part));
    }

    /// <summary>
    ///     The harness-relative path a fixture renders a single agent to. A global install renders relative
    ///     to its target's global root, so it takes a bare path; a project install keeps the target directory.
    /// </summary>
    internal static string AgentRelativePath(SquadDeploymentScope scope, string fileName)
    {
        return scope == SquadDeploymentScope.Global
            ? $"agents/{fileName}"
            : $".codex/agents/{fileName}";
    }

    internal static SortedDictionary<string, byte[]> Snapshot(string root)
    {
        SortedDictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            snapshot.Add(relativePath, ReadAllBytesAllowingWrites(path));
        }

        return snapshot;
    }

    internal static SortedDictionary<string, TreeEntry> CaptureTree(string root)
    {
        SortedDictionary<string, TreeEntry> snapshot = new(StringComparer.Ordinal);
        CaptureTreeDirectory(new DirectoryInfo(root), root, snapshot);
        return snapshot;
    }

    internal static void CaptureTreeDirectory(
        DirectoryInfo directory,
        string root,
        IDictionary<string, TreeEntry> snapshot)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            string relativePath = Path.GetRelativePath(root, entry.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (entry.LinkTarget is not null)
            {
                bool isDirectoryLink = OperatingSystem.IsWindows()
                    ? (entry.Attributes & FileAttributes.Directory) != 0
                    : Directory.Exists(entry.FullName);
                snapshot.Add(
                    relativePath,
                    new TreeEntry(
                        isDirectoryLink
                            ? TreeEntryKind.DirectorySymbolicLink
                            : TreeEntryKind.FileSymbolicLink,
                        null,
                        entry.LinkTarget));
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                snapshot.Add(relativePath, new TreeEntry(TreeEntryKind.Directory, null, null));
                CaptureTreeDirectory(childDirectory, root, snapshot);
                continue;
            }

            snapshot.Add(
                relativePath,
                new TreeEntry(
                    TreeEntryKind.File,
                    ReadAllBytesAllowingWrites(entry.FullName),
                    null));
        }
    }

    internal static byte[] ReadAllBytesAllowingWrites(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static void AssertSnapshotsEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string? path, byte[]? expectedBytes) in expected)
            Assert.True(
                actual[path].SequenceEqual(expectedBytes),
                $"File '{path}' changed. Expected {Digest(expectedBytes)}, actual {Digest(actual[path])}.");
    }

    internal static void AssertTreesEqual(
        IReadOnlyDictionary<string, TreeEntry> expected,
        IReadOnlyDictionary<string, TreeEntry> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string? path, TreeEntry? expectedEntry) in expected)
        {
            TreeEntry actualEntry = actual[path];
            Assert.Equal(expectedEntry.Kind, actualEntry.Kind);
            Assert.Equal(expectedEntry.LinkTarget, actualEntry.LinkTarget);
            if (expectedEntry.Content is null)
            {
                Assert.Null(actualEntry.Content);
                continue;
            }

            Assert.NotNull(actualEntry.Content);
            Assert.True(
                expectedEntry.Content.SequenceEqual(actualEntry.Content),
                $"File '{path}' changed. Expected {Digest(expectedEntry.Content)}, actual {Digest(actualEntry.Content)}.");
        }
    }

    internal static void RestoreTree(
        string root,
        IReadOnlyDictionary<string, TreeEntry> snapshot)
    {
        DeleteTreeContents(new DirectoryInfo(root));

        foreach ((string? relativePath, TreeEntry _) in snapshot
                     .Where(pair => pair.Value.Kind == TreeEntryKind.Directory)
                     .OrderBy(pair => PathDepth(pair.Key)))
            Directory.CreateDirectory(ToPlatformPath(root, relativePath));

        foreach ((string? relativePath, TreeEntry? entry) in snapshot
                     .Where(pair => pair.Value.Kind == TreeEntryKind.File))
        {
            string path = ToPlatformPath(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Assert.IsType<byte[]>(entry.Content));
        }

        foreach ((string? relativePath, TreeEntry? entry) in snapshot.Where(pair =>
                     pair.Value.Kind is TreeEntryKind.FileSymbolicLink or
                         TreeEntryKind.DirectorySymbolicLink))
        {
            string path = ToPlatformPath(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string linkTarget = Assert.IsType<string>(entry.LinkTarget);
            if (entry.Kind == TreeEntryKind.DirectorySymbolicLink)
                Directory.CreateSymbolicLink(path, linkTarget);
            else
                File.CreateSymbolicLink(path, linkTarget);
        }
    }

    internal static void DeleteTreeContents(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().ToArray())
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                    Directory.Delete(entry.FullName);
                else
                    File.Delete(entry.FullName);
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                DeleteTreeContents(childDirectory);
                childDirectory.Delete();
            }
            else
            {
                entry.Delete();
            }
        }
    }

    internal static string ToPlatformPath(string root, string relativePath)
    {
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static int PathDepth(string relativePath)
    {
        return relativePath.Count(character => character == '/');
    }

    internal static void RestoreSnapshot(
        string root,
        IReadOnlyDictionary<string, byte[]> snapshot)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(path);

        foreach (string directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            Directory.Delete(directory, false);

        foreach ((string? relativePath, byte[]? content) in snapshot)
        {
            string path = Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }

    internal static string Digest(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    internal static void AssertLockEqual(SquadLock expected, SquadLock actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.SquadVersion, actual.SquadVersion);
        Assert.Equal(expected.CliVersion, actual.CliVersion);
        Assert.Equal(expected.McpVersion, actual.McpVersion);
        Assert.Equal(expected.Bundle, actual.Bundle);
        Assert.Equal(expected.Targets, actual.Targets);
        Assert.Equal(expected.Exclusions, actual.Exclusions);
        Assert.Equal(expected.Translation, actual.Translation);
        Assert.Equal(expected.BundleDigest, actual.BundleDigest);
        Assert.Equal(expected.AssetDigest, actual.AssetDigest);
        Assert.Equal(expected.Apm, actual.Apm);
    }

    internal static void AssertReceiptEqual(SquadReceipt expected, SquadReceipt actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.TargetRoot, actual.TargetRoot);
        Assert.Equal(expected.InstalledAtUtc, actual.InstalledAtUtc);
        Assert.Equal(expected.Degradations, actual.Degradations);
        Assert.Equal(expected.Files, actual.Files);
    }

    internal enum TreeEntryKind
    {
        File,
        Directory,
        FileSymbolicLink,
        DirectorySymbolicLink
    }

    internal sealed record TreeEntry(
        TreeEntryKind Kind,
        byte[]? Content,
        string? LinkTarget);

    internal sealed record ActiveTransitionScenario(
        SquadDeploymentPlan Plan,
        string Subject);

    internal sealed record ClaimMutationScenario(
        SquadDeploymentPlan Plan,
        string Subject,
        string LiveRelativePath);

    internal sealed record AfterImageRestoreScenario(
        SquadDeploymentPlan Plan,
        string Subject);

    internal sealed record RestoreArtifactScenario(
        SquadDeploymentPlan Plan,
        SquadTransactionStepKind MutationBoundary,
        string ArtifactPath,
        string IntentPath,
        string LiveRelativePath,
        TreeEntry ExpectedLiveAfterImage);

    internal sealed class FakeGlobalRootResolver(IReadOnlyDictionary<SquadTarget, string> roots)
        : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return roots.TryGetValue(target, out string? root)
                ? root
                : throw new ArgumentOutOfRangeException(nameof(target), target, "No root mapped for target.");
        }
    }

    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return value;
        }
    }

    /// <summary>
    ///     Records where the durable intent, the staged bytes and the backups live while a global
    ///     transaction runs. The intent, staging and backups all belong in the home journal, never beside the
    ///     target root and never in the portable global state directory.
    /// </summary>
    internal sealed class GlobalTransactionLocationObserver(
        string targetRoot,
        string stateDirectory,
        string journalDirectory) : ISquadTransactionObserver
    {
        public bool SawDurableIntentInJournal { get; private set; }

        public bool SawStagingInJournal { get; private set; }

        public bool SawBackupInJournal { get; private set; }

        public bool SawStagingOrBackupOnTargetRoot { get; private set; }

        public bool SawStagingOrBackupInState { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            SawDurableIntentInJournal |= Directory.Exists(journalDirectory) &&
                                         Directory.EnumerateFiles(
                                             journalDirectory,
                                             "intent.json",
                                             SearchOption.AllDirectories).Any();
            if (step.Kind == SquadTransactionStepKind.FileStaged)
            {
                SawStagingInJournal |= HasDirectoryNamed(journalDirectory, "staging");
                SawStagingOrBackupOnTargetRoot |= HasDirectoryNamed(targetRoot, "staging");
                SawStagingOrBackupInState |= HasDirectoryNamed(stateDirectory, "staging");
            }

            if (step.Kind == SquadTransactionStepKind.FileBackedUp)
            {
                SawBackupInJournal |= HasDirectoryNamed(journalDirectory, "backups");
                SawStagingOrBackupOnTargetRoot |= HasDirectoryNamed(targetRoot, "backups");
                SawStagingOrBackupInState |= HasDirectoryNamed(stateDirectory, "backups");
            }
        }
    }

    internal sealed class BlockingObserver(SquadTransactionStepKind blockedStep) :
        ISquadTransactionObserver,
        IDisposable
    {
        public ManualResetEventSlim Reached { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public void Dispose()
        {
            Reached.Dispose();
            Release.Dispose();
        }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != blockedStep)
                return;

            Reached.Set();
            Release.Wait();
        }
    }

    internal sealed class RecordingObserver(string root) : ISquadTransactionObserver
    {
        private readonly string _transactionDirectory = Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction");

        public List<SquadTransactionStep> Steps { get; } = [];

        public bool SawIntentJournal { get; private set; }

        public bool SawStagedFile { get; private set; }

        public bool SawBackupFile { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            Steps.Add(step);
            SawIntentJournal |= File.Exists(Path.Combine(_transactionDirectory, "intent.json"));
            SawStagedFile |= Directory.Exists(Path.Combine(_transactionDirectory, "staging")) &&
                             Directory.EnumerateFiles(
                                 Path.Combine(_transactionDirectory, "staging"),
                                 "*",
                                 SearchOption.AllDirectories).Any();
            SawBackupFile |= Directory.Exists(Path.Combine(_transactionDirectory, "backups")) &&
                             Directory.EnumerateFiles(
                                 Path.Combine(_transactionDirectory, "backups"),
                                 "*",
                                 SearchOption.AllDirectories).Any();
        }
    }

    internal sealed class CheckpointRecordingObserver : ISquadTransactionCheckpointObserver
    {
        public List<SquadTransactionStep> Steps { get; } = [];

        public List<SquadTransactionCheckpoint> Checkpoints { get; } = [];

        public void AfterStep(SquadTransactionStep step)
        {
            Steps.Add(step);
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            Checkpoints.Add(checkpoint);
        }
    }

    internal sealed class FailingObserver(string root, int failureSequence) : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, byte[]>? InterruptedSnapshot { get; private set; }

        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Sequence == failureSequence)
            {
                InterruptedSnapshot = Snapshot(root);
                InterruptedTreeSnapshot = CaptureTree(root);
                throw new InjectedSquadTransactionFailure(step.Sequence);
            }
        }
    }

    internal sealed class LifecycleStepFailingObserver(
        string root,
        SquadTransactionStepKind checkpoint) : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != checkpoint)
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    internal sealed class RestoreArtifactTamperingObserver(
        string artifactPath,
        SquadTransactionStepKind mutationBoundary) : ISquadTransactionObserver
    {
        public byte[] CorruptBytes { get; } = Encoding.UTF8.GetBytes(
            $"corrupted restore artifact after {mutationBoundary}");

        public bool MutatedArtifact { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != mutationBoundary)
                return;

            Assert.True(
                File.Exists(artifactPath),
                $"Prepared restore artifact '{artifactPath}' was not published.");
            File.WriteAllBytes(artifactPath, CorruptBytes);
            MutatedArtifact = true;
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    internal sealed class CheckpointFailingObserver(
        string root,
        SquadTransactionCheckpointKind checkpoint) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint)
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    internal sealed class PostIntentReplacementObserver(
        string root,
        SquadStateStore store,
        SquadDeploymentScope scope,
        string relativePath,
        SquadTransactionStepKind checkpoint) : ISquadTransactionObserver
    {
        public byte[] ExternalBytes { get; } = Encoding.UTF8.GetBytes(
            $"external replacement after {checkpoint}");

        public string ReplacedPath => checkpoint switch
        {
            SquadTransactionStepKind.FileApplied => ToPlatformPath(root, relativePath),
            SquadTransactionStepKind.LockApplied => store.ResolveLockPath(root, scope),
            SquadTransactionStepKind.ReceiptApplied => store.ResolveReceiptPath(root, scope),
            _ => throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint, null)
        };

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != checkpoint)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(ReplacedPath)!);
            File.WriteAllBytes(ReplacedPath, ExternalBytes);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    internal sealed class ExternalChildObserver(
        string root,
        string generatedPath,
        string externalPath) : ISquadTransactionObserver
    {
        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != SquadTransactionStepKind.FileApplied ||
                !string.Equals(step.RelativePath, generatedPath, StringComparison.Ordinal))
                return;

            Write(root, externalPath, "external operator note");
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    internal sealed class PreparedArtifactsObserver(
        string root,
        string targetRelativePath) : ISquadTransactionCheckpointObserver
    {
        public bool ObservedPreparedCheckpoint { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (checkpoint.Kind != SquadTransactionCheckpointKind.Prepared)
                return;

            Assert.Equal("installed body", Read(root, targetRelativePath));
            string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
            string[] temporaryArtifacts = Directory.EnumerateFileSystemEntries(
                    transactionDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(temporaryArtifacts);

            string intentPath = Path.Combine(transactionDirectory, "intent.json");
            using JsonDocument journal = JsonDocument.Parse(ReadAllBytesAllowingWrites(intentPath));
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
                byte[] bytes = ReadAllBytesAllowingWrites(artifactPath);
                Assert.Equal(bytes.LongLength, artifact.GetProperty("byteLength").GetInt64());
                Assert.Equal(Digest(bytes), artifact.GetProperty("sha256").GetString());
            }

            ObservedPreparedCheckpoint = true;
        }
    }

    internal sealed class StagedArtifactTamperingObserver(
        string root,
        string relativePath,
        string artifact) : ISquadTransactionCheckpointObserver
    {
        private bool _tampered;

        public bool SawAffectedApplyCheckpoint { get; private set; }

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
                Assert.True(File.Exists(stagePath), $"Prepared stage '{stagePath}' was not published.");
                File.WriteAllText(
                    stagePath,
                    $"changed prepared {artifact}",
                    new UTF8Encoding(false));
                _tampered = true;
            }
        }
    }

    internal sealed class LeafRaceObserver(
        string root,
        string relativePath,
        string racedNodeKind) : ISquadTransactionCheckpointObserver
    {
        private bool _raced;

        public TreeEntry? RacedEntry { get; private set; }

        public string ExternalCanaryPath { get; private set; } = string.Empty;

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (_raced || checkpoint.Kind != SquadTransactionCheckpointKind.Prepared)
                return;

            string targetPath = ToPlatformPath(root, relativePath);
            DeleteLeaf(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            switch (racedNodeKind)
            {
                case "file":
                    File.WriteAllText(
                        targetPath,
                        "external raced file",
                        new UTF8Encoding(false));
                    ExternalCanaryPath = targetPath;
                    break;
                case "directory":
                    Directory.CreateDirectory(targetPath);
                    ExternalCanaryPath = Path.Combine(targetPath, "external-child.txt");
                    File.WriteAllText(
                        ExternalCanaryPath,
                        "external raced directory child",
                        new UTF8Encoding(false));
                    break;
                case "link":
                    ExternalCanaryPath = Path.Combine(root, "external-link-target.txt");
                    File.WriteAllText(
                        ExternalCanaryPath,
                        "external raced link target",
                        new UTF8Encoding(false));
                    File.CreateSymbolicLink(targetPath, ExternalCanaryPath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown raced node kind '{racedNodeKind}'.");
            }

            RacedEntry = CaptureTree(root)[relativePath];
            _raced = true;
        }

        private static void DeleteLeaf(string path)
        {
            FileInfo entry = new(path);
            if (entry.LinkTarget is not null)
            {
                File.Delete(path);
                return;
            }

            DirectoryInfo directory = new(path);
            if (directory.LinkTarget is not null)
            {
                Directory.Delete(path);
                return;
            }

            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    internal sealed class PreparedBoundaryCorruptionObserver(
        string root,
        string relativePath,
        string corruption) : ISquadTransactionCheckpointObserver
    {
        public bool CorruptedPreparedArtifacts { get; private set; }

        public bool SawLeafOrStateMutation { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (!CorruptedPreparedArtifacts)
                return;

            SawLeafOrStateMutation |= step.Kind is
                SquadTransactionStepKind.FileApplied or
                SquadTransactionStepKind.LockApplied or
                SquadTransactionStepKind.ReceiptApplied;
        }

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
                        File.Delete(stagePath);
                        break;
                    case "mutated":
                        File.WriteAllText(
                            stagePath,
                            "mutated after prepared publication",
                            new UTF8Encoding(false));
                        break;
                    case "extra":
                        Write(
                            transactionDirectory,
                            "unexpected/closed-set-extra.bin",
                            "undeclared prepared artifact");
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

    internal sealed class ActiveTransitionCrashObserver(
        string root,
        SquadTransactionCheckpointKind checkpoint,
        string subject) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint ||
                !string.Equals(value.RelativePath, subject, StringComparison.Ordinal))
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    internal sealed class OriginalClaimStageTamperingObserver(
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

        public bool SawOriginalClaimed { get; private set; }

        public bool SawAffectedAfterImage { get; private set; }

        public bool SawAffectedPublicApply { get; private set; }

        public TreeEntry? MutatedStageEntry { get; private set; }

        public string IntentPath => Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");

        public string StageRelativePath => subject switch
        {
            "target" => $".kyber-weave/.squad-transaction/staging/codex/{targetRelativePath}",
            "lock" => ".kyber-weave/.squad-transaction/state-staging/lock",
            "receipt" => ".kyber-weave/.squad-transaction/state-staging/receipt",
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };

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
                File.Exists(stagePath),
                $"Prepared stage '{stagePath}' must exist at OriginalClaimed.");
            switch (mutation)
            {
                case "edit-bytes":
                    File.WriteAllText(
                        stagePath,
                        $"changed {subject} stage after claim",
                        new UTF8Encoding(false));
                    break;
                case "replacement-link":
                    File.Delete(stagePath);
                    File.CreateSymbolicLink(stagePath, "replacement-stage-target");
                    break;
                case "replacement-directory":
                    File.Delete(stagePath);
                    Directory.CreateDirectory(stagePath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown claim-stage mutation '{mutation}'.");
            }

            MutatedStageEntry = CaptureTree(root)[StageRelativePath];
        }
    }

    internal sealed class OriginalClaimTamperingObserver(
        string root,
        string subject,
        string mutation) : ISquadTransactionCheckpointObserver
    {
        public bool SawOriginalClaimed { get; private set; }

        public bool SawAffectedAfterImage { get; private set; }

        public bool SawAffectedPublicApply { get; private set; }

        public string ClaimRelativePath { get; private set; } = string.Empty;

        public TreeEntry? MutatedClaimEntry { get; private set; }

        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public string IntentPath => Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");

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

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!string.Equals(checkpoint.RelativePath, subject, StringComparison.Ordinal))
                return;

            SawAffectedAfterImage |=
                checkpoint.Kind == SquadTransactionCheckpointKind.AfterImagePublished;
            if (checkpoint.Kind != SquadTransactionCheckpointKind.OriginalClaimed)
                return;

            SawOriginalClaimed = true;
            using JsonDocument intent = JsonDocument.Parse(ReadAllBytesAllowingWrites(IntentPath));
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
                    File.WriteAllText(
                        claimPath,
                        $"changed {subject} claim bytes",
                        new UTF8Encoding(false));
                    break;
                case "retarget-file-link":
                    File.Delete(claimPath);
                    File.CreateSymbolicLink(claimPath, "retargeted-file-claim");
                    break;
                case "retarget-directory-link":
                    DeleteLeafEntry(claimPath);
                    Directory.CreateSymbolicLink(claimPath, "retargeted-directory-claim");
                    break;
                case "replace-directory":
                    File.Delete(claimPath);
                    Directory.CreateDirectory(claimPath);
                    break;
                case "replace-file":
                    Directory.Delete(claimPath, true);
                    File.WriteAllText(
                        claimPath,
                        $"replacement {subject} claim file",
                        new UTF8Encoding(false));
                    break;
                case "unchanged":
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown claim mutation '{mutation}'.");
            }

            MutatedClaimEntry = CaptureTree(root)[ClaimRelativePath];
            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(checkpoint.Sequence);
        }
    }

    internal sealed class AfterImageCrashObserver(
        string root,
        string subject) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (checkpoint.Kind != SquadTransactionCheckpointKind.AfterImagePublished ||
                !string.Equals(checkpoint.RelativePath, subject, StringComparison.Ordinal))
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(checkpoint.Sequence);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Standard exception constructors required by CA1032.")]
    internal sealed class InjectedSquadTransactionFailure : Exception
    {
        public InjectedSquadTransactionFailure()
        {
        }

        public InjectedSquadTransactionFailure(string message)
            : base(message)
        {
        }

        public InjectedSquadTransactionFailure(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public InjectedSquadTransactionFailure(int sequence)
            : base($"Injected failure after transaction step {sequence}.")
        {
            Sequence = sequence;
        }

        public int Sequence { get; }
    }

    internal sealed class RichTransactionFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        private RichTransactionFixture()
        {
            const string installedBody = "installed conductor";
            const string obsoleteBody = "installed obsolete";
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".codex", "agents"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".codex", "preserved-empty"));
            string canonicalPath = System.IO.Path.Combine(Path, "canonical", "conductor.toml");
            Write(Path, "canonical/conductor.toml", installedBody);
            string agentDirectory = System.IO.Path.Combine(Path, ".codex", "agents");
            File.CreateSymbolicLink(
                System.IO.Path.Combine(agentDirectory, "conductor.toml"),
                System.IO.Path.GetRelativePath(agentDirectory, canonicalPath));
            Write(Path, ".cursor/agents/obsolete.md", obsoleteBody);
            Receipt = Receipt(
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest(installedBody),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".cursor/agents/obsolete.md",
                    Digest(obsoleteBody),
                    "cursor",
                    false));
            WriteState(Path, Lock(), Receipt);
        }

        public string Path => _temp.Path;

        public SquadReceipt Receipt { get; }

        public void Dispose()
        {
            _temp.Dispose();
        }

        public static RichTransactionFixture Create()
        {
            return new RichTransactionFixture();
        }

        public SquadDeploymentPlan CreateUpdatePlan()
        {
            return RealSquadDeployment.Planner.CreateUpdate(
                Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [
                    Rendered(".codex/agents/conductor.toml", "updated conductor"),
                    Rendered(".warp/roles/reviewer.md", "new reviewer", "warp")
                ],
                Receipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));
        }
    }

    internal sealed class TransactionFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        private TransactionFixture()
        {
            RelativePath = ".codex/agents/conductor.toml";
            Write(Path, RelativePath, "installed body");
            Receipt = Receipt(
                new SquadOwnedFile(
                    RelativePath,
                    Digest("installed body"),
                    "codex",
                    false));
            WriteState(Path, Lock(), Receipt);
        }

        public string Path => _temp.Path;

        public string RelativePath { get; }

        public SquadReceipt Receipt { get; }

        public void Dispose()
        {
            _temp.Dispose();
        }

        public static TransactionFixture Create()
        {
            return new TransactionFixture();
        }

        public SquadDeploymentPlan CreateUpdatePlan()
        {
            return RealSquadDeployment.Planner.CreateUpdate(
                Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(RelativePath, "updated body")],
                Receipt,
                [new SquadDegradation("codex", "conductor", "model-profile-narrowed")],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));
        }
    }
}
