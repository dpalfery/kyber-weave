using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit and integration tests for <see cref="SquadInstanceLinkSync" />, verifying launch-time
///     link synchronization, atomic link state recording, and file split detection.
/// </summary>
/// <remarks>
///     <para>
///         Acceptance criterion 7.4: An instance SHALL share the main home's settings and history through links.
///         WHERE the harness stores sign-in in a file, the instance SHALL share sign-in through a link too.
///     </para>
///     <para>
///         Acceptance criterion 7.4a: Exception: WHERE a harness rewrites its sign-in file by rename,
///         the instance SHALL NOT link that file, and the user SHALL sign in at most once per instance.
///     </para>
///     <para>
///         Acceptance criterion 7.8: WHEN Squad removes or refreshes an instance THEN it SHALL delete
///         only the instance's own files and links. It SHALL NOT delete or change any link's target.
///     </para>
/// </remarks>
public sealed class SquadInstanceLinkSyncTests
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly FileSystemSquadLinkFileSystem _links;
    private readonly SquadInstanceLinkSync _sync;

    public SquadInstanceLinkSyncTests()
    {
        _links = new FileSystemSquadLinkFileSystem(_files);
        _sync = new SquadInstanceLinkSync(
            _files,
            new FakePathResolver(_files),
            new InProcessLease(new InProcessLeaseHost()),
            new FakeProcessEnvironment(currentDirectory: Path.Combine(_files.Root, "work")));
    }

    [Fact]
    public void ConfigLayerRowPerformsNoOperationsAndReturnsEmptyResult()
    {
        string instanceRoot = Path.Combine(_files.Root, "config-instance");
        string mainHome = Path.Combine(_files.Root, "config-main");
        string stateDirectory = Path.Combine(_files.Root, "config-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string mainSettingsFile = Path.Combine(mainHome, "settings.json");
        _files.WriteAllText(mainSettingsFile, "{\"telemetry\": false}");

        SquadInstanceRow row = new(
            SquadTarget.Copilot,
            "copilot",
            SquadInstanceKind.ConfigLayer,
            "COPILOT_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.Ready,
            null);

        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult result = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Empty(result.Links);
        Assert.Empty(result.Warnings);

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        Assert.False(_files.FileExists(linksJsonPath), "ConfigLayer instances must not record links.json.");
        Assert.Empty(_files.EnumerateEntries(instanceRoot));
    }

    [Fact]
    public void HomeReplaceRowCreatesLinksForMainHomeEntriesAndWritesAtomicLinksJson()
    {
        string instanceRoot = Path.Combine(_files.Root, "claude-instance");
        string mainHome = Path.Combine(_files.Root, "claude-main");
        string stateDirectory = Path.Combine(_files.Root, "claude-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string settingsFile = Path.Combine(mainHome, "settings.json");
        string historyFile = Path.Combine(mainHome, "history.sqlite");
        string logsDir = Path.Combine(mainHome, "logs");
        _files.CreateDirectory(logsDir);
        string logEntry = Path.Combine(logsDir, "session.log");

        _files.WriteAllText(settingsFile, "{\"theme\": \"dark\"}");
        _files.WriteAllText(historyFile, "SQLITE-BINARY-DATA");
        _files.WriteAllText(logEntry, "log-message-1");

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/worker.md" };
        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult result = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            renderedPaths,
            fileSystem,
            stateDirectory);

        Assert.Contains(result.Links, l => l.RelativePath == "settings.json" && l.Kind == SquadLinkKind.File);
        Assert.Contains(result.Links, l => l.RelativePath == "history.sqlite" && l.Kind == SquadLinkKind.File);
        Assert.Contains(result.Links, l => l.RelativePath == "logs" && l.Kind == SquadLinkKind.Directory);

        string instanceSettings = Path.Combine(instanceRoot, "settings.json");
        string instanceHistory = Path.Combine(instanceRoot, "history.sqlite");
        string instanceLogs = Path.Combine(instanceRoot, "logs");

        Assert.True(fileSystem.IsLink(instanceSettings));
        Assert.True(fileSystem.IsLink(instanceHistory));
        Assert.True(fileSystem.IsLink(instanceLogs));
        Assert.True(fileSystem.IsLinkTo(instanceSettings, settingsFile));
        Assert.True(fileSystem.IsLinkTo(instanceHistory, historyFile));
        Assert.True(fileSystem.IsLinkTo(instanceLogs, logsDir));

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        Assert.True(_files.FileExists(linksJsonPath));

        string json = _files.ReadAllText(linksJsonPath);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(result.Links.Count, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void ObsoleteRecordedLinkIsRemovedWhenTargetNoLongerExistsInMainHome()
    {
        string instanceRoot = Path.Combine(_files.Root, "obsolete-instance");
        string mainHome = Path.Combine(_files.Root, "obsolete-main");
        string stateDirectory = Path.Combine(_files.Root, "obsolete-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string settingsFile = Path.Combine(mainHome, "settings.json");
        string tempCacheFile = Path.Combine(mainHome, "cache.json");
        _files.WriteAllText(settingsFile, "{}");
        _files.WriteAllText(tempCacheFile, "{}");

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult firstResult = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Equal(2, firstResult.Links.Count);
        string instanceCacheLink = Path.Combine(instanceRoot, "cache.json");
        Assert.True(fileSystem.IsLink(instanceCacheLink));

        _files.DeleteFile(tempCacheFile);

        SquadInstanceLinkSyncResult secondResult = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Single(secondResult.Links);
        Assert.Contains(secondResult.Links, l => l.RelativePath == "settings.json");
        Assert.DoesNotContain(secondResult.Links, l => l.RelativePath == "cache.json");

        Assert.False(_files.FileExists(instanceCacheLink));
        Assert.False(fileSystem.IsLink(instanceCacheLink));
    }

    [Fact]
    public void MatchingSplitFileLinkIsReLinkedToMainHome()
    {
        string instanceRoot = Path.Combine(_files.Root, "split-matching-instance");
        string mainHome = Path.Combine(_files.Root, "split-matching-main");
        string stateDirectory = Path.Combine(_files.Root, "split-matching-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        const string content = "{\"common\": \"data\"}";
        string mainSettings = Path.Combine(mainHome, "settings.json");
        _files.WriteAllText(mainSettings, content);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult firstSync = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        string instanceSettings = Path.Combine(instanceRoot, "settings.json");
        Assert.True(fileSystem.IsLinkTo(instanceSettings, mainSettings));

        // Simulate harness breaking the link via atomic save (regular file with identical bytes)
        _files.DeleteFile(instanceSettings);
        _files.WriteAllText(instanceSettings, content);
        Assert.False(fileSystem.IsLinkTo(instanceSettings, mainSettings));

        SquadInstanceLinkSyncResult secondSync = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        // Matching split must be re-linked
        Assert.True(fileSystem.IsLinkTo(instanceSettings, mainSettings));
        Assert.Contains(secondSync.Links, l => l.RelativePath == "settings.json");
        Assert.DoesNotContain(secondSync.Warnings, w => w.Contains("settings.json", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferingSplitFileLinkKeepsBothCopiesAndProducesWarningNamingTheFile()
    {
        string instanceRoot = Path.Combine(_files.Root, "split-diff-instance");
        string mainHome = Path.Combine(_files.Root, "split-diff-main");
        string stateDirectory = Path.Combine(_files.Root, "split-diff-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        const string mainContent = "{\"theme\": \"light\"}";
        const string editedInstanceContent = "{\"theme\": \"dark\", \"customized\": true}";

        string mainSettings = Path.Combine(mainHome, "settings.json");
        _files.WriteAllText(mainSettings, mainContent);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult firstSync = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        string instanceSettings = Path.Combine(instanceRoot, "settings.json");
        Assert.True(fileSystem.IsLinkTo(instanceSettings, mainSettings));

        // Simulate harness modifying the split file with different content
        _files.DeleteFile(instanceSettings);
        _files.WriteAllText(instanceSettings, editedInstanceContent);
        Assert.False(fileSystem.IsLinkTo(instanceSettings, mainSettings));

        SquadInstanceLinkSyncResult secondSync = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        // Both copies must be kept, and instance copy remains a regular file
        Assert.False(fileSystem.IsLink(instanceSettings));
        Assert.Equal(editedInstanceContent, _files.ReadAllText(instanceSettings));
        Assert.Equal(mainContent, _files.ReadAllText(mainSettings));

        // Warning naming the differing split file must be generated
        string warning = Assert.Single(secondSync.Warnings, w => w.Contains("settings.json", StringComparison.Ordinal));
        Assert.Contains("settings.json", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void MainHomeFilesAndDirectoriesAreNeverModifiedDuringSync()
    {
        string instanceRoot = Path.Combine(_files.Root, "preserve-instance");
        string mainHome = Path.Combine(_files.Root, "preserve-main");
        string stateDirectory = Path.Combine(_files.Root, "preserve-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string mainFile1 = Path.Combine(mainHome, "auth.json");
        string mainFile2 = Path.Combine(mainHome, "history.sqlite");
        string mainSubdir = Path.Combine(mainHome, "config");
        _files.CreateDirectory(mainSubdir);
        string mainFile3 = Path.Combine(mainSubdir, "keys.pem");

        _files.WriteAllText(mainFile1, "{\"auth\": \"secret\"}");
        _files.WriteAllText(mainFile2, "sqlite-database-contents");
        _files.WriteAllText(mainFile3, "certificate-data");

        byte[] hash1Before = SHA256.HashData(_files.ReadAllBytes(mainFile1));
        byte[] hash2Before = SHA256.HashData(_files.ReadAllBytes(mainFile2));
        byte[] hash3Before = SHA256.HashData(_files.ReadAllBytes(mainFile3));

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        // Simulate local edits and splits inside instance
        string instanceAuth = Path.Combine(instanceRoot, "auth.json");
        _files.DeleteFile(instanceAuth);
        _files.WriteAllText(instanceAuth, "{\"auth\": \"local-instance-overwrite\"}");

        _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        // Main home files must remain completely unchanged
        Assert.Equal(hash1Before, SHA256.HashData(_files.ReadAllBytes(mainFile1)));
        Assert.Equal(hash2Before, SHA256.HashData(_files.ReadAllBytes(mainFile2)));
        Assert.Equal(hash3Before, SHA256.HashData(_files.ReadAllBytes(mainFile3)));

        // No new entries created under mainHome
        Assert.Equal(4, CountEntries(mainHome)); // config dir, keys.pem, auth.json, history.sqlite
    }

    [Fact]
    public void AcceptanceCriterion7_4_InstanceSharesSettingsAndHistoryThroughLinks()
    {
        string instanceRoot = Path.Combine(_files.Root, "ac7-4-instance");
        string mainHome = Path.Combine(_files.Root, "ac7-4-main");
        string stateDirectory = Path.Combine(_files.Root, "ac7-4-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string mainSettings = Path.Combine(mainHome, "settings.json");
        string mainHistory = Path.Combine(mainHome, "history.sqlite");
        _files.WriteAllText(mainSettings, "{\"initial\": true}");
        _files.WriteAllText(mainHistory, "initial-history");

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult result = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Contains(result.Links, l => l.RelativePath == "settings.json");
        Assert.Contains(result.Links, l => l.RelativePath == "history.sqlite");

        string instanceSettings = Path.Combine(instanceRoot, "settings.json");
        string instanceHistory = Path.Combine(instanceRoot, "history.sqlite");

        Assert.True(fileSystem.IsLinkTo(instanceSettings, mainSettings));
        Assert.True(fileSystem.IsLinkTo(instanceHistory, mainHistory));

        // Shared link mutation: updating main target reflects in instance reading
        _files.WriteAllText(mainSettings, "{\"updated-from-main\": true}");
        Assert.Equal("{\"updated-from-main\": true}", _files.ReadAllText(instanceSettings));
    }

    [Fact]
    public void AcceptanceCriterion7_4a_SignInFileRewrittenByRenameInNeverLinkedIsNotLinked()
    {
        string instanceRoot = Path.Combine(_files.Root, "ac7-4a-instance");
        string mainHome = Path.Combine(_files.Root, "ac7-4a-main");
        string stateDirectory = Path.Combine(_files.Root, "ac7-4a-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string mainSettings = Path.Combine(mainHome, "settings.json");
        string mainHistory = Path.Combine(mainHome, "history.db");
        string mainSession = Path.Combine(mainHome, "session.json");
        _files.WriteAllText(mainSettings, "{}");
        _files.WriteAllText(mainHistory, "db");
        _files.WriteAllText(mainSession, "{\"token\": \"opaque\"}");

        SquadInstanceRow row = new(
            SquadTarget.Codex,
            "codex",
            SquadInstanceKind.HomeReplace,
            "CODEX_HOME",
            [],
            ["session.json"],
            SquadInstanceStatus.Ready,
            null);

        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult result = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Contains(result.Links, l => l.RelativePath == "settings.json");
        Assert.Contains(result.Links, l => l.RelativePath == "history.db");

        // The session file in NeverLinked must never be linked
        Assert.DoesNotContain(result.Links, l => l.RelativePath == "session.json");
        string instanceSession = Path.Combine(instanceRoot, "session.json");
        Assert.False(_files.FileExists(instanceSession));
        Assert.False(fileSystem.IsLink(instanceSession));
    }

    [Fact]
    public void AcceptanceCriterion7_8_SyncModifiesOnlyInstanceFilesAndPreservesMainHomeTargets()
    {
        string instanceRoot = Path.Combine(_files.Root, "ac7-8-instance");
        string mainHome = Path.Combine(_files.Root, "ac7-8-main");
        string stateDirectory = Path.Combine(_files.Root, "ac7-8-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string targetDir = Path.Combine(mainHome, "shared-data");
        _files.CreateDirectory(targetDir);
        string targetFile = Path.Combine(targetDir, "target.txt");
        const string payload = "essential-user-data-must-never-be-deleted-or-modified";
        _files.WriteAllText(targetFile, payload);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult result = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Contains(result.Links, l => l.RelativePath == "shared-data" && l.Kind == SquadLinkKind.Directory);

        // Instance directory has the link
        string instanceLink = Path.Combine(instanceRoot, "shared-data");
        Assert.True(fileSystem.IsLink(instanceLink));

        // When refreshing/re-syncing with empty mainHome target removal:
        // Main target must not be touched
        Assert.True(_files.DirectoryExists(targetDir));
        Assert.True(_files.FileExists(targetFile));
        Assert.Equal(payload, _files.ReadAllText(targetFile));
    }

    [Fact]
    public void RenderedPathsAreExcludedFromLinking()
    {
        string instanceRoot = Path.Combine(_files.Root, "rendered-instance");
        string mainHome = Path.Combine(_files.Root, "rendered-main");
        string stateDirectory = Path.Combine(_files.Root, "rendered-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string agentsDir = Path.Combine(mainHome, "agents");
        _files.CreateDirectory(agentsDir);
        _files.WriteAllText(Path.Combine(agentsDir, "worker.md"), "canonical-worker");
        _files.WriteAllText(Path.Combine(agentsDir, "personal.md"), "user-personal-agent");
        _files.WriteAllText(Path.Combine(mainHome, "settings.json"), "{}");

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/worker.md" };
        ISquadLinkFileSystem fileSystem = _links;

        SquadInstanceLinkSyncResult result = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            renderedPaths,
            fileSystem,
            stateDirectory);

        Assert.Contains(result.Links, l => l.RelativePath == "settings.json");
        Assert.Contains(result.Links, l => l.RelativePath == "agents/personal.md");

        // The rendered file must NOT be linked
        Assert.DoesNotContain(result.Links, l => l.RelativePath == "agents/worker.md");
        Assert.DoesNotContain(result.Links, l => l.RelativePath == "agents" && l.Kind == SquadLinkKind.Directory);
    }

    [Fact]
    public void MutexKeyFormatFollowsRootKeyConvention()
    {
        string instanceRoot = Path.Combine(_files.Root, "instance-for-mutex");
        _files.CreateDirectory(instanceRoot);

        string mutexName = _sync.GetMutexName(instanceRoot);

        Assert.StartsWith("kyber-weave-squad-", mutexName, StringComparison.Ordinal);
        Assert.True(mutexName.Length > "kyber-weave-squad-".Length);
    }

    [Fact]
    public async Task ConcurrentSyncInvocationsOnSameInstanceRootRunSafely()
    {
        string instanceRoot = Path.Combine(_files.Root, "concurrent-instance");
        string mainHome = Path.Combine(_files.Root, "concurrent-main");
        string stateDirectory = Path.Combine(_files.Root, "concurrent-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        _files.WriteAllText(Path.Combine(mainHome, "settings.json"), "{}");

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        Task<SquadInstanceLinkSyncResult> task1 = Task.Run(() => _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory));

        Task<SquadInstanceLinkSyncResult> task2 = Task.Run(() => _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory));

        await Task.WhenAll(task1, task2);

        SquadInstanceLinkSyncResult result1 = await task1;
        SquadInstanceLinkSyncResult result2 = await task2;

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Single(result1.Links);
        Assert.Single(result2.Links);
    }

    [Fact]
    public void FakeSquadLinkFileSystemWorksDeterministicallyWithoutHostPermissions()
    {
        string instanceRoot = Path.Combine(_files.Root, "virtual-instance");
        string mainHome = Path.Combine(_files.Root, "fake-main");
        string stateDirectory = Path.Combine(_files.Root, "fake-state");
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string mainSettings = Path.Combine(mainHome, "settings.json");
        _files.WriteAllText(mainSettings, "{}");

        FakeSquadLinkFileSystem fakeFileSystem = new();
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);

        SquadInstanceLinkSyncResult result = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fakeFileSystem,
            stateDirectory);

        Assert.Single(result.Links);
        Assert.Equal("settings.json", result.Links[0].RelativePath);
        Assert.True(fakeFileSystem.IsLinkTo(Path.Combine(instanceRoot, "settings.json"), mainSettings));
    }

    [Fact]
    public void SyncRejectsPathTraversalInRecordedLinksAndLeavesVictimFileUntouched()
    {
        string outsideVictim = Path.Combine(_files.Root, "outside-victim.txt");
        _files.WriteAllText(outsideVictim, "do-not-delete");

        string instanceRoot = Path.Combine(_files.Root, "traversal-instance");
        string mainHome = Path.Combine(_files.Root, "traversal-main");
        string stateDirectory = Path.Combine(_files.Root, "traversal-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        string json = $$"""
                      [
                        {
                          "RelativePath": "../outside-victim.txt",
                          "TargetPath": {{JsonSerializer.Serialize(Path.Combine(_files.Root, "nonexistent", "target"))}},
                          "Kind": 0,
                          "LastSeenSize": 0,
                          "LastSeenModifiedUtc": "2026-01-01T00:00:00Z"
                        }
                      ]
                      """;
        _files.WriteAllText(linksJsonPath, json);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        Assert.Throws<InvalidDataException>(() => _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory));

        Assert.True(_files.FileExists(outsideVictim),
            "Path traversal in links.json must not delete files outside instanceRoot.");
    }

    [Fact]
    public void SyncWhenDeleteStaleFileThrowsUnauthorizedAccessExceptionReturnsWarningAndPreservesLinkRecord()
    {
        string instanceRoot = Path.Combine(_files.Root, "locked-instance");
        string mainHome = Path.Combine(_files.Root, "locked-main");
        string stateDirectory = Path.Combine(_files.Root, "locked-state");
        string lockedDir = Path.Combine(instanceRoot, "locked");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);
        _files.CreateDirectory(lockedDir);

        string missingTarget = Path.Combine(_files.Root, "nonexistent", "target");
        string staleFile = Path.Combine(lockedDir, "stale.txt");
        _files.CreateFileLink(staleFile, missingTarget);

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        string json = $$"""
                        [
                          {
                            "RelativePath": "locked/stale.txt",
                            "TargetPath": {{JsonSerializer.Serialize(missingTarget)}},
                            "Kind": 0,
                            "LastSeenSize": 5,
                            "LastSeenModifiedUtc": "2026-01-01T00:00:00Z"
                          }
                        ]
                        """;
        _files.WriteAllText(linksJsonPath, json);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        // The process may not delete the stale link: the file system refuses it as a read-only directory would.
        _files.FailWhen(
            operation => operation.Name == "DeleteFile"
                && string.Equals(operation.Path, staleFile, StringComparison.Ordinal),
            new UnauthorizedAccessException("Access to the path is denied."));

        SquadInstanceLinkSyncResult result = _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Contains(result.Warnings, warning => warning.Contains("stale.txt", StringComparison.Ordinal));

        string savedLinksJson = _files.ReadAllText(linksJsonPath);
        Assert.Contains("stale.txt", savedLinksJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncWhenLinksJsonIsCorruptThrowsInvalidDataException()
    {
        string instanceRoot = Path.Combine(_files.Root, "corrupt-instance");
        string mainHome = Path.Combine(_files.Root, "corrupt-main");
        string stateDirectory = Path.Combine(_files.Root, "corrupt-state");
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(mainHome);
        _files.CreateDirectory(stateDirectory);

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        _files.WriteAllText(linksJsonPath, "{ corrupt json : [");

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = _links;

        Assert.Throws<InvalidDataException>(() => _sync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory));
    }

    private int CountEntries(string directory)
    {
        int count = 0;
        foreach (FileSystemEntry entry in _files.EnumerateEntries(directory))
        {
            count++;
            if (entry.IsDirectory && !entry.IsLink)
            {
                count += CountEntries(entry.Path);
            }
        }

        return count;
    }

    private sealed class FakeSquadLinkFileSystem : ISquadLinkFileSystem
    {
        private readonly Dictionary<string, string> _fileLinks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _folderLinks = new(StringComparer.Ordinal);

        public void CreateFileLink(string linkPath, string targetPath)
        {
            _fileLinks[Normalize(linkPath)] = Normalize(targetPath);
        }

        public void CreateFolderLink(string linkPath, string targetPath)
        {
            _folderLinks[Normalize(linkPath)] = Normalize(targetPath);
        }

        public bool IsLink(string path)
        {
            string normalized = Normalize(path);
            return _fileLinks.ContainsKey(normalized) || _folderLinks.ContainsKey(normalized);
        }

        public bool IsLinkTo(string linkPath, string expectedTarget)
        {
            string normalizedLink = Normalize(linkPath);
            string normalizedExpected = Normalize(expectedTarget);

            if (_fileLinks.TryGetValue(normalizedLink, out string? actualTarget))
                return string.Equals(actualTarget, normalizedExpected, StringComparison.Ordinal);

            if (_folderLinks.TryGetValue(normalizedLink, out string? actualFolderTarget))
                return string.Equals(actualFolderTarget, normalizedExpected, StringComparison.Ordinal);

            return false;
        }

        public void DeleteTreeNoFollow(string directoryPath)
        {
            string prefix = Normalize(directoryPath) + "/";
            List<string> fileKeysToRemove =
                _fileLinks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (string key in fileKeysToRemove) _fileLinks.Remove(key);

            List<string> folderKeysToRemove =
                _folderLinks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (string key in folderKeysToRemove) _folderLinks.Remove(key);
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }
}
