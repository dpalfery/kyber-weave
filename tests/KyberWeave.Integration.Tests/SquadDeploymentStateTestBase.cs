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
public abstract class SquadDeploymentStateTestBase : SquadStateDocumentFixtures
{

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

    internal static void AssertActiveLeaseConflict(Exception? exception)
    {
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
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

    internal static SquadDeploymentFile Rendered(
        string relativePath,
        string content,
        string target = "codex")
    {
        return new SquadDeploymentFile(relativePath, Encoding.UTF8.GetBytes(content), target);
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
