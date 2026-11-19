using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Testing.Squad;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Cli.Commands.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// The deployment-state fixtures over the in-memory file system: one fake file system, path resolver, path semantics,
/// lease host and clock per test, and a scope per tree a case works in. Every root derives from
/// <see cref="InMemoryFileSystem.Root"/>, so a case runs unchanged on every platform.
/// </summary>
public abstract class InMemoryDeploymentStateTestBase : SquadStateDocumentFixtures
{
    private static int _next;

    /// <summary>Gets the agent file the codex target owns in the collision fixtures.</summary>
    internal static readonly SquadOwnedFile CodexOwned =
        new(".codex/agents/conductor.toml", Digest("codex body"), "codex", false);

    /// <summary>Gets the agent file the cursor target owns in the collision fixtures.</summary>
    internal static readonly SquadOwnedFile CursorOwned =
        new(".cursor/agents/reviewer.md", Digest("cursor body"), "cursor", false);

    /// <summary>Gets one degradation per fixture target.</summary>
    internal static readonly SquadDegradation[] CodexAndCursorDegradations =
    [
        new("codex", "conductor", "codex-code"),
        new("cursor", "reviewer", "cursor-code")
    ];

    protected InMemoryDeploymentStateTestBase()
    {
        Files = new InMemoryFileSystem(caseSensitive: true);
        string stateRoot = Path.Combine(
            Files.Root,
            "kw",
            "work",
            $"case-{Interlocked.Increment(ref _next):x}");
        State = new InMemorySquadState(stateRoot: stateRoot, existingFiles: Files);
        Paths = State.Paths;
        Semantics = State.Semantics;
        Environment = State.Environment;
        Planner = State.Planner;
        Scope = new DeploymentScope(stateRoot);
    }

    /// <summary>Gets the state everything shares, anchored on <see cref="Root"/>.</summary>
    internal InMemorySquadState State { get; }

    /// <summary>Gets the file system every scope in this test shares.</summary>
    internal InMemoryFileSystem Files { get; }

    /// <summary>Gets the path seam over <see cref="Files"/>.</summary>
    internal FakePathResolver Paths { get; }

    /// <summary>Gets the deployment path semantics over <see cref="Paths"/>.</summary>
    internal SquadFileSystemPathSemantics Semantics { get; }

    /// <summary>Gets the process environment.</summary>
    internal FakeProcessEnvironment Environment { get; }

    /// <summary>Gets the deployment planner over <see cref="Files"/>.</summary>
    internal SquadDeploymentPlanner Planner { get; }

    /// <summary>Gets the scope the case works in unless it asks for another.</summary>
    internal DeploymentScope Scope { get; }

    /// <summary>Gets the root of <see cref="Scope"/>.</summary>
    internal string Root => Scope.Path;

    /// <summary>Creates a directory of its own on this test's file system.</summary>
    /// <returns>The scope.</returns>
    internal DeploymentScope NewScope()
    {
        string path = Path.Combine(
            Files.Root,
            "kw",
            "work",
            $"case-{Interlocked.Increment(ref _next):x}");
        Files.CreateDirectory(path);
        return new DeploymentScope(path);
    }

    /// <summary>Gets the state store anchored on a directory, with its home beneath it.</summary>
    /// <param name="applicationData">The application-data directory; the home sits beneath it as the product's does.</param>
    /// <returns>The store.</returns>
    internal SquadStateStore StoreAt(string applicationData) =>
        new(
            new FakeSquadUserPaths(applicationData),
            Files,
            Semantics,
            Paths,
            Environment,
            new InProcessLease(State.LeaseHost),
            TimeSpan.FromMilliseconds(200));

    /// <summary>Gets a deployment transaction over the state anchored on a directory.</summary>
    /// <param name="applicationData">The application-data directory.</param>
    /// <param name="observer">Told of each durable step; none when null.</param>
    /// <returns>The transaction.</returns>
    internal SquadTransaction TransactionAt(string applicationData, ISquadTransactionObserver? observer = null) =>
        new(
            StoreAt(applicationData),
            Files,
            Semantics,
            new InProcessLease(State.LeaseHost),
            new FakeIdGenerator(),
            observer);

    /// <summary>Writes a text file under a root, creating its directory.</summary>
    /// <param name="root">The root.</param>
    /// <param name="relativePath">The '/'-separated path beneath it.</param>
    /// <param name="content">The text.</param>
    internal void Write(string root, string relativePath, string content)
    {
        string path = ToPlatformPath(root, relativePath);
        Files.CreateDirectory(Path.GetDirectoryName(path)!);
        Files.WriteAllText(path, content);
    }

    /// <summary>Reads a text file under a root.</summary>
    /// <param name="root">The root.</param>
    /// <param name="relativePath">The '/'-separated path beneath it.</param>
    /// <returns>The text.</returns>
    internal string Read(string root, string relativePath) =>
        Files.ReadAllText(ToPlatformPath(root, relativePath));

    /// <summary>Writes a project's lock and receipt where the store resolves them.</summary>
    /// <param name="root">The project root, which is also its application-data directory.</param>
    /// <param name="squadLock">The lock.</param>
    /// <param name="receipt">The receipt.</param>
    internal void WriteState(string root, SquadLock squadLock, SquadReceipt receipt)
    {
        SquadStateStore store = StoreAt(root);
        string lockPath = store.ResolveLockPath(root, SquadDeploymentScope.Project);
        string receiptPath = store.ResolveReceiptPath(root, SquadDeploymentScope.Project);
        Files.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        Files.WriteAllText(lockPath, store.SerializeLock(squadLock));
        Files.WriteAllText(receiptPath, store.SerializeReceipt(receipt));
    }

    /// <summary>Expresses a store-resolved state file as the '/'-separated key a tree capture uses.</summary>
    /// <param name="root">The root the capture is measured from.</param>
    /// <param name="statePath">The state file.</param>
    /// <returns>The key.</returns>
    internal string StateRelativePath(string root, string statePath) =>
        Path.GetRelativePath(Semantics.Canonicalize(root), statePath)
            .Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Describes one deployed file.</summary>
    /// <param name="relativePath">The path beneath its target root.</param>
    /// <param name="content">The rendered text.</param>
    /// <param name="target">The owning target token.</param>
    /// <returns>The file.</returns>
    internal static SquadDeploymentFile Rendered(
        string relativePath,
        string content,
        string target = "codex") =>
        new(relativePath, Encoding.UTF8.GetBytes(content), target);

    /// <summary>Joins a '/'-separated relative path onto a root.</summary>
    /// <param name="root">The root.</param>
    /// <param name="relativePath">The '/'-separated path.</param>
    /// <returns>The platform path.</returns>
    internal static string ToPlatformPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Reads a file as raw JSON.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The document.</returns>
    internal JsonObject ReadJsonObject(string path) =>
        JsonNode.Parse(Files.ReadAllText(path))!.AsObject();

    /// <summary>Writes a JSON document to a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="document">The document.</param>
    internal void WriteJsonObject(string path, JsonObject document) =>
        Files.WriteAllText(path, document.ToJsonString() + "\n");

    /// <summary>Captures the files beneath a root as path to bytes.</summary>
    /// <param name="root">The root.</param>
    /// <returns>The files, keyed by '/'-separated relative path.</returns>
    internal SortedDictionary<string, byte[]> Snapshot(string root)
    {
        SortedDictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
        foreach (string path in Files.EnumerateFiles(root, "*", recursive: true))
        {
            snapshot.Add(
                Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                Files.ReadAllBytes(path));
        }

        return snapshot;
    }

    /// <summary>Captures a whole tree, including links and directories.</summary>
    /// <param name="root">The root.</param>
    /// <returns>The tree, keyed by '/'-separated relative path.</returns>
    internal SortedDictionary<string, TreeEntry> CaptureTree(string root) =>
        CaptureTree(Files, root);

    /// <summary>Captures a whole tree through an explicit file system.</summary>
    /// <param name="files">The file system.</param>
    /// <param name="root">The root.</param>
    /// <returns>The tree, keyed by '/'-separated relative path.</returns>
    internal static SortedDictionary<string, TreeEntry> CaptureTree(
        InMemoryFileSystem files,
        string root)
    {
        SortedDictionary<string, TreeEntry> snapshot = new(StringComparer.Ordinal);
        CaptureDirectory(files, root, root, snapshot);
        return snapshot;
    }

    private static void CaptureDirectory(
        InMemoryFileSystem files,
        string directory,
        string root,
        IDictionary<string, TreeEntry> snapshot)
    {
        foreach (FileSystemEntry entry in files.EnumerateEntries(directory))
        {
            string relativePath = Path.GetRelativePath(root, entry.Path)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (entry.IsLink)
            {
                snapshot.Add(
                    relativePath,
                    new TreeEntry(
                        entry.IsDirectory
                            ? TreeEntryKind.DirectorySymbolicLink
                            : TreeEntryKind.FileSymbolicLink,
                        null,
                        files.GetLinkTarget(entry.Path)));
                continue;
            }

            if (entry.IsDirectory)
            {
                snapshot.Add(relativePath, new TreeEntry(TreeEntryKind.Directory, null, null));
                CaptureDirectory(files, entry.Path, root, snapshot);
                continue;
            }

            snapshot.Add(
                relativePath,
                new TreeEntry(TreeEntryKind.File, files.ReadAllBytes(entry.Path), null));
        }
    }

    /// <summary>Asserts two file snapshots hold the same bytes at the same paths.</summary>
    /// <param name="expected">The expected snapshot.</param>
    /// <param name="actual">The actual snapshot.</param>
    internal static void AssertSnapshotsEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string path, byte[] expectedBytes) in expected)
        {
            Assert.True(
                actual[path].SequenceEqual(expectedBytes),
                $"File '{path}' changed. Expected {Digest(expectedBytes)}, actual {Digest(actual[path])}.");
        }
    }

    /// <summary>Asserts two whole trees are identical.</summary>
    /// <param name="expected">The expected tree.</param>
    /// <param name="actual">The actual tree.</param>
    internal static void AssertTreesEqual(
        IReadOnlyDictionary<string, TreeEntry> expected,
        IReadOnlyDictionary<string, TreeEntry> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string path, TreeEntry expectedEntry) in expected)
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

    /// <summary>Asserts one tree entry matches.</summary>
    /// <param name="expected">The expected entry.</param>
    /// <param name="actual">The actual entry.</param>
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
            $"Entry changed. Expected {Digest(expected.Content)}, actual {Digest(actual.Content)}.");
    }

    /// <summary>Replaces a tree with a captured one, as a killed process leaves it to a later run.</summary>
    /// <param name="root">The root.</param>
    /// <param name="snapshot">The tree to restore.</param>
    internal void RestoreTree(string root, IReadOnlyDictionary<string, TreeEntry> snapshot)
    {
        foreach (FileSystemEntry entry in Files.EnumerateEntries(root).ToList())
        {
            Files.DeleteTreeNoFollow(entry.Path);
        }

        foreach ((string relativePath, TreeEntry _) in snapshot
                     .Where(pair => pair.Value.Kind == TreeEntryKind.Directory)
                     .OrderBy(pair => PathDepth(pair.Key))
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Files.CreateDirectory(ToPlatformPath(root, relativePath));
        }

        foreach ((string relativePath, TreeEntry entry) in snapshot
                     .Where(pair => pair.Value.Kind == TreeEntryKind.File))
        {
            string path = ToPlatformPath(root, relativePath);
            Files.CreateDirectory(Path.GetDirectoryName(path)!);
            Files.WriteAllBytes(path, Assert.IsType<byte[]>(entry.Content));
        }

        foreach ((string relativePath, TreeEntry entry) in snapshot.Where(pair =>
                     pair.Value.Kind is TreeEntryKind.FileSymbolicLink or
                         TreeEntryKind.DirectorySymbolicLink))
        {
            string path = ToPlatformPath(root, relativePath);
            Files.CreateDirectory(Path.GetDirectoryName(path)!);
            string linkTarget = Assert.IsType<string>(entry.LinkTarget);
            if (entry.Kind == TreeEntryKind.DirectorySymbolicLink)
            {
                Files.CreateDirectoryLink(path, linkTarget);
            }
            else
            {
                Files.CreateFileLink(path, linkTarget);
            }
        }
    }

    /// <summary>Replaces a root's files with a captured snapshot.</summary>
    /// <param name="root">The root.</param>
    /// <param name="snapshot">The files to restore.</param>
    internal void RestoreSnapshot(string root, IReadOnlyDictionary<string, byte[]> snapshot)
    {
        foreach (FileSystemEntry entry in Files.EnumerateEntries(root).ToList())
        {
            Files.DeleteTreeNoFollow(entry.Path);
        }

        foreach ((string relativePath, byte[] content) in snapshot)
        {
            string path = ToPlatformPath(root, relativePath);
            Files.CreateDirectory(Path.GetDirectoryName(path)!);
            Files.WriteAllBytes(path, content);
        }
    }

    /// <summary>Counts the path separators in a relative path.</summary>
    /// <param name="relativePath">The path.</param>
    /// <returns>The depth.</returns>
    internal static int PathDepth(string relativePath) =>
        relativePath.Count(character => character == '/');

    /// <summary>Whether a directory at or below a root holds a directory of a given name.</summary>
    /// <param name="root">The root.</param>
    /// <param name="name">The directory name.</param>
    /// <returns>True when one exists.</returns>
    internal bool HasDirectoryNamed(string root, string name) =>
        Files.DirectoryExists(root) && Files.EnumerateDirectories(root)
            .Any(path => string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal));

    /// <summary>Builds a project whose live target or state file is the node kind a claim case names.</summary>
    /// <param name="root">The project root.</param>
    /// <param name="subject">The artifact: <c>target</c>, <c>lock</c> or <c>receipt</c>.</param>
    /// <param name="originalKind">The node kind: <c>file</c>, <c>file-link</c>, <c>directory</c> or <c>directory-link</c>.</param>
    /// <returns>The plan and the live path it changes.</returns>
    internal ClaimMutationScenario CreateClaimMutationScenario(
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
        SquadStateStore store = StoreAt(root);
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
        SquadDeploymentPlan plan = Planner.CreateUpdate(
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

    /// <summary>Builds the after-image restore scenario of one artifact role.</summary>
    /// <param name="root">The project root.</param>
    /// <param name="artifactRole">The artifact role the case names.</param>
    /// <returns>The plan and the subject it publishes.</returns>
    internal AfterImageRestoreScenario CreateAfterImageRestoreScenario(
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

    /// <summary>Replaces a live node with the node kind a claim case names.</summary>
    /// <param name="root">The project root.</param>
    /// <param name="livePath">The live node.</param>
    /// <param name="subject">The artifact: <c>target</c>, <c>lock</c> or <c>receipt</c>.</param>
    /// <param name="originalKind">The node kind.</param>
    internal void ConfigureOriginalNode(
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
        Files.CreateDirectory(fixtureDirectory);
        switch (originalKind)
        {
            case "file-link":
                {
                    string linkTargetPath = Path.Combine(fixtureDirectory, $"{subject}-file.txt");
                    string content = string.Equals(subject, "target", StringComparison.Ordinal)
                        ? "installed claim body"
                        : $"original {subject} state";
                    Files.CreateDirectory(Path.GetDirectoryName(linkTargetPath)!);
                    Files.WriteAllText(linkTargetPath, content);
                    Files.CreateDirectory(Path.GetDirectoryName(livePath)!);
                    Files.CreateFileLink(
                        livePath,
                        Path.GetRelativePath(Path.GetDirectoryName(livePath)!, linkTargetPath));
                    break;
                }
            case "directory-link":
                {
                    string linkTargetPath = Path.Combine(fixtureDirectory, $"{subject}-directory");
                    Files.CreateDirectory(linkTargetPath);
                    Files.CreateDirectory(Path.GetDirectoryName(livePath)!);
                    Files.CreateDirectoryLink(
                        livePath,
                        Path.GetRelativePath(Path.GetDirectoryName(livePath)!, linkTargetPath));
                    break;
                }
            case "directory":
                Files.CreateDirectory(livePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(originalKind), originalKind, null);
        }
    }

    /// <summary>Deletes a leaf node, or the link itself when it is one.</summary>
    /// <param name="path">The node.</param>
    internal void DeleteLeafEntry(string path)
    {
        if (Files.GetLinkTarget(path) is not null || Files.PathExists(path))
            Files.DeleteTreeNoFollow(path);
    }

    /// <summary>Builds the restore artifacts one restore case consumes.</summary>
    /// <param name="root">The project root.</param>
    /// <param name="artifactRole">The artifact role the case names.</param>
    /// <returns>The plan and the artifact, journal and live paths it works on.</returns>
    internal RestoreArtifactScenario CreateRestoreArtifactScenario(
        string root,
        string artifactRole)
    {
        const string targetRelativePath = ".codex/agents/conductor.toml";
        const string installedBody = "installed body";
        string targetPath = ToPlatformPath(root, targetRelativePath);
        Files.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (string.Equals(artifactRole, "target-link-metadata", StringComparison.Ordinal))
        {
            const string canonicalRelativePath = "canonical/conductor.toml";
            string canonicalPath = Path.Combine(root, "canonical", "conductor.toml");
            Write(root, canonicalRelativePath, installedBody);
            Files.CreateFileLink(
                targetPath,
                Path.GetRelativePath(Path.GetDirectoryName(targetPath)!, canonicalPath));
        }
        else
        {
            Files.WriteAllText(targetPath, installedBody);
        }

        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            targetRelativePath,
            Digest(installedBody),
            "codex",
            false));
        WriteState(root, Lock(), previousReceipt);
        SquadDeploymentPlan plan = Planner.CreateUpdate(
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
        SquadStateStore store = StoreAt(root);
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

    /// <summary>A clock fixed at one instant.</summary>
    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

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

    /// <summary>A tree that holds every path a case wrote, captured and restored as a whole.</summary>
    internal sealed class DeploymentScope(string path)
    {
        /// <summary>Gets the scope's root.</summary>
        internal string Path { get; } = path;
    }

    /// <summary>A project deployment with one installed agent file, for the transaction cases to update.</summary>
    internal sealed class TransactionFixture
    {
        private readonly InMemoryDeploymentStateTestBase _host;

        private TransactionFixture(InMemoryDeploymentStateTestBase host)
        {
            _host = host;
            Path = host.NewScope().Path;
            RelativePath = ".codex/agents/conductor.toml";
            host.Write(Path, RelativePath, "installed body");
            Receipt = Receipt(
                new SquadOwnedFile(
                    RelativePath,
                    Digest("installed body"),
                    "codex",
                    false));
            host.WriteState(Path, Lock(), Receipt);
        }

        public string Path { get; }

        public string RelativePath { get; }

        public SquadReceipt Receipt { get; }

        public static TransactionFixture Create(InMemoryDeploymentStateTestBase host)
        {
            ArgumentNullException.ThrowIfNull(host);
            return new TransactionFixture(host);
        }

        public SquadDeploymentPlan CreateUpdatePlan() =>
            _host.Planner.CreateUpdate(
                Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(RelativePath, "updated body")],
                Receipt,
                [new SquadDegradation("codex", "conductor", "model-profile-narrowed")],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));
    }

    /// <summary>A project deployment with a linked agent, an obsolete agent and an empty preserved directory.</summary>
    internal sealed class RichTransactionFixture
    {
        private readonly InMemoryDeploymentStateTestBase _host;

        private RichTransactionFixture(InMemoryDeploymentStateTestBase host)
        {
            _host = host;
            const string installedBody = "installed conductor";
            const string obsoleteBody = "installed obsolete";
            Path = host.NewScope().Path;
            string agentDirectory = System.IO.Path.Combine(Path, ".codex", "agents");
            host.Files.CreateDirectory(agentDirectory);
            host.Files.CreateDirectory(System.IO.Path.Combine(Path, ".codex", "preserved-empty"));
            string canonicalPath = System.IO.Path.Combine(Path, "canonical", "conductor.toml");
            host.Write(Path, "canonical/conductor.toml", installedBody);
            host.Files.CreateFileLink(
                System.IO.Path.Combine(agentDirectory, "conductor.toml"),
                System.IO.Path.GetRelativePath(agentDirectory, canonicalPath));
            host.Write(Path, ".cursor/agents/obsolete.md", obsoleteBody);
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
            host.WriteState(Path, Lock(), Receipt);
        }

        public string Path { get; }

        public SquadReceipt Receipt { get; }

        public static RichTransactionFixture Create(InMemoryDeploymentStateTestBase host)
        {
            ArgumentNullException.ThrowIfNull(host);
            return new RichTransactionFixture(host);
        }

        public SquadDeploymentPlan CreateUpdatePlan() =>
            _host.Planner.CreateUpdate(
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

    [SuppressMessage(
        "ReSharper",
        "UnusedMember.Local",
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

    /// <summary>Records every durable step a transaction reports, and what it could see at that point.</summary>
    internal sealed class RecordingObserver(InMemoryFileSystem files, string root) : ISquadTransactionObserver
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
            SawIntentJournal |= files.FileExists(Path.Combine(_transactionDirectory, "intent.json"));
            SawStagedFile |= files.DirectoryExists(Path.Combine(_transactionDirectory, "staging")) &&
                             files.EnumerateFiles(
                                 Path.Combine(_transactionDirectory, "staging"),
                                 "*",
                                 recursive: true).Any();
            SawBackupFile |= files.DirectoryExists(Path.Combine(_transactionDirectory, "backups")) &&
                             files.EnumerateFiles(
                                 Path.Combine(_transactionDirectory, "backups"),
                                 "*",
                                 recursive: true).Any();
        }
    }

    /// <summary>Stops the run after one numbered step, capturing the tree the process would leave behind.</summary>
    internal sealed class FailingObserver(InMemoryFileSystem files, string root, int failureSequence)
        : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, byte[]>? InterruptedSnapshot { get; private set; }

        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Sequence != failureSequence)
                return;

            InterruptedSnapshot = SnapshotOf(files, root);
            InterruptedTreeSnapshot = CaptureTree(files, root);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    /// <summary>Stops the run after the first step of one kind.</summary>
    internal sealed class LifecycleStepFailingObserver(
        InMemoryFileSystem files,
        string root,
        SquadTransactionStepKind checkpoint) : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != checkpoint)
                return;

            InterruptedTreeSnapshot = CaptureTree(files, root);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    /// <summary>Corrupts a restore artifact once the run reaches the step that consumes it.</summary>
    internal sealed class RestoreArtifactTamperingObserver(
        InMemoryFileSystem files,
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
                files.FileExists(artifactPath),
                $"Prepared restore artifact '{artifactPath}' was not published.");
            files.WriteAllBytes(artifactPath, CorruptBytes);
            MutatedArtifact = true;
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    /// <summary>Stops the run at the after-image checkpoint of one artifact.</summary>
    internal sealed class AfterImageCrashObserver(
        InMemoryFileSystem files,
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

            InterruptedTreeSnapshot = CaptureTree(files, root);
            throw new InjectedSquadTransactionFailure(checkpoint.Sequence);
        }
    }

    /// <summary>The files beneath a root as path to bytes, through an explicit file system.</summary>
    internal static SortedDictionary<string, byte[]> SnapshotOf(InMemoryFileSystem files, string root)
    {
        SortedDictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
        foreach (string path in files.EnumerateFiles(root, "*", recursive: true))
        {
            snapshot.Add(
                Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                files.ReadAllBytes(path));
        }

        return snapshot;
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
}
