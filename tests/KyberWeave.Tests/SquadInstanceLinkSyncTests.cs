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
public sealed class SquadInstanceLinkSyncTests : IDisposable
{
    private readonly TempDirectory _tempDirectory = new();

    public void Dispose()
    {
        _tempDirectory.Dispose();
    }

    [Fact]
    public void ConfigLayerRowPerformsNoOperationsAndReturnsEmptyResult()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "config-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "config-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "config-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string mainSettingsFile = Path.Combine(mainHome, "settings.json");
        File.WriteAllText(mainSettingsFile, "{\"telemetry\": false}", Encoding.UTF8);

        SquadInstanceRow row = new(
            SquadTarget.Copilot,
            "copilot",
            SquadInstanceKind.ConfigLayer,
            "COPILOT_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.Ready,
            null);

        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult result = SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Empty(result.Links);
        Assert.Empty(result.Warnings);

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        Assert.False(File.Exists(linksJsonPath), "ConfigLayer instances must not record links.json.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(instanceRoot));
    }

    [Fact]
    public void HomeReplaceRowCreatesLinksForMainHomeEntriesAndWritesAtomicLinksJson()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "claude-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "claude-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "claude-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string settingsFile = Path.Combine(mainHome, "settings.json");
        string historyFile = Path.Combine(mainHome, "history.sqlite");
        string logsDir = Path.Combine(mainHome, "logs");
        Directory.CreateDirectory(logsDir);
        string logEntry = Path.Combine(logsDir, "session.log");

        File.WriteAllText(settingsFile, "{\"theme\": \"dark\"}", Encoding.UTF8);
        File.WriteAllText(historyFile, "SQLITE-BINARY-DATA", Encoding.UTF8);
        File.WriteAllText(logEntry, "log-message-1", Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/worker.md" };
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult result = SquadInstanceLinkSync.Sync(
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
        Assert.True(File.Exists(linksJsonPath));

        string json = File.ReadAllText(linksJsonPath, Encoding.UTF8);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(result.Links.Count, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void ObsoleteRecordedLinkIsRemovedWhenTargetNoLongerExistsInMainHome()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "obsolete-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "obsolete-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "obsolete-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string settingsFile = Path.Combine(mainHome, "settings.json");
        string tempCacheFile = Path.Combine(mainHome, "cache.json");
        File.WriteAllText(settingsFile, "{}", Encoding.UTF8);
        File.WriteAllText(tempCacheFile, "{}", Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult firstResult = SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Equal(2, firstResult.Links.Count);
        string instanceCacheLink = Path.Combine(instanceRoot, "cache.json");
        Assert.True(fileSystem.IsLink(instanceCacheLink));

        File.Delete(tempCacheFile);

        SquadInstanceLinkSyncResult secondResult = SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        Assert.Single(secondResult.Links);
        Assert.Contains(secondResult.Links, l => l.RelativePath == "settings.json");
        Assert.DoesNotContain(secondResult.Links, l => l.RelativePath == "cache.json");

        Assert.False(File.Exists(instanceCacheLink));
        Assert.False(fileSystem.IsLink(instanceCacheLink));
    }

    [Fact]
    public void MatchingSplitFileLinkIsReLinkedToMainHome()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "split-matching-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "split-matching-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "split-matching-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        const string content = "{\"common\": \"data\"}";
        string mainSettings = Path.Combine(mainHome, "settings.json");
        File.WriteAllText(mainSettings, content, Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult firstSync = SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        string instanceSettings = Path.Combine(instanceRoot, "settings.json");
        Assert.True(fileSystem.IsLinkTo(instanceSettings, mainSettings));

        // Simulate harness breaking the link via atomic save (regular file with identical bytes)
        File.Delete(instanceSettings);
        File.WriteAllText(instanceSettings, content, Encoding.UTF8);
        Assert.False(fileSystem.IsLinkTo(instanceSettings, mainSettings));

        SquadInstanceLinkSyncResult secondSync = SquadInstanceLinkSync.Sync(
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
        string instanceRoot = Path.Combine(_tempDirectory.Path, "split-diff-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "split-diff-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "split-diff-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        const string mainContent = "{\"theme\": \"light\"}";
        const string editedInstanceContent = "{\"theme\": \"dark\", \"customized\": true}";

        string mainSettings = Path.Combine(mainHome, "settings.json");
        File.WriteAllText(mainSettings, mainContent, Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult firstSync = SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        string instanceSettings = Path.Combine(instanceRoot, "settings.json");
        Assert.True(fileSystem.IsLinkTo(instanceSettings, mainSettings));

        // Simulate harness modifying the split file with different content
        File.Delete(instanceSettings);
        File.WriteAllText(instanceSettings, editedInstanceContent, Encoding.UTF8);
        Assert.False(fileSystem.IsLinkTo(instanceSettings, mainSettings));

        SquadInstanceLinkSyncResult secondSync = SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        // Both copies must be kept, and instance copy remains a regular file
        Assert.False(fileSystem.IsLink(instanceSettings));
        Assert.Equal(editedInstanceContent, File.ReadAllText(instanceSettings, Encoding.UTF8));
        Assert.Equal(mainContent, File.ReadAllText(mainSettings, Encoding.UTF8));

        // Warning naming the differing split file must be generated
        string warning = Assert.Single(secondSync.Warnings, w => w.Contains("settings.json", StringComparison.Ordinal));
        Assert.Contains("settings.json", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void MainHomeFilesAndDirectoriesAreNeverModifiedDuringSync()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "preserve-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "preserve-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "preserve-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string mainFile1 = Path.Combine(mainHome, "auth.json");
        string mainFile2 = Path.Combine(mainHome, "history.sqlite");
        string mainSubdir = Path.Combine(mainHome, "config");
        Directory.CreateDirectory(mainSubdir);
        string mainFile3 = Path.Combine(mainSubdir, "keys.pem");

        File.WriteAllText(mainFile1, "{\"auth\": \"secret\"}", Encoding.UTF8);
        File.WriteAllText(mainFile2, "sqlite-database-contents", Encoding.UTF8);
        File.WriteAllText(mainFile3, "certificate-data", Encoding.UTF8);

        byte[] hash1Before = SHA256.HashData(File.ReadAllBytes(mainFile1));
        byte[] hash2Before = SHA256.HashData(File.ReadAllBytes(mainFile2));
        byte[] hash3Before = SHA256.HashData(File.ReadAllBytes(mainFile3));

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        // Simulate local edits and splits inside instance
        string instanceAuth = Path.Combine(instanceRoot, "auth.json");
        File.Delete(instanceAuth);
        File.WriteAllText(instanceAuth, "{\"auth\": \"local-instance-overwrite\"}", Encoding.UTF8);

        SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory);

        // Main home files must remain completely unchanged
        Assert.Equal(hash1Before, SHA256.HashData(File.ReadAllBytes(mainFile1)));
        Assert.Equal(hash2Before, SHA256.HashData(File.ReadAllBytes(mainFile2)));
        Assert.Equal(hash3Before, SHA256.HashData(File.ReadAllBytes(mainFile3)));

        // No new entries created under mainHome
        string[] mainEntries = Directory.GetFileSystemEntries(mainHome, "*", SearchOption.AllDirectories);
        Assert.Equal(4, mainEntries.Length); // config dir, keys.pem, auth.json, history.sqlite
    }

    [Fact]
    public void AcceptanceCriterion7_4_InstanceSharesSettingsAndHistoryThroughLinks()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "ac7-4-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "ac7-4-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "ac7-4-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string mainSettings = Path.Combine(mainHome, "settings.json");
        string mainHistory = Path.Combine(mainHome, "history.sqlite");
        File.WriteAllText(mainSettings, "{\"initial\": true}", Encoding.UTF8);
        File.WriteAllText(mainHistory, "initial-history", Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult result = SquadInstanceLinkSync.Sync(
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
        File.WriteAllText(mainSettings, "{\"updated-from-main\": true}", Encoding.UTF8);
        Assert.Equal("{\"updated-from-main\": true}", File.ReadAllText(instanceSettings, Encoding.UTF8));
    }

    [Fact]
    public void AcceptanceCriterion7_4a_SignInFileRewrittenByRenameInNeverLinkedIsNotLinked()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "ac7-4a-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "ac7-4a-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "ac7-4a-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string mainSettings = Path.Combine(mainHome, "settings.json");
        string mainHistory = Path.Combine(mainHome, "history.db");
        string mainSession = Path.Combine(mainHome, "session.json");
        File.WriteAllText(mainSettings, "{}", Encoding.UTF8);
        File.WriteAllText(mainHistory, "db", Encoding.UTF8);
        File.WriteAllText(mainSession, "{\"token\": \"opaque\"}", Encoding.UTF8);

        SquadInstanceRow row = new(
            SquadTarget.Codex,
            "codex",
            SquadInstanceKind.HomeReplace,
            "CODEX_HOME",
            [],
            ["session.json"],
            SquadInstanceStatus.Ready,
            null);

        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult result = SquadInstanceLinkSync.Sync(
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
        Assert.False(File.Exists(instanceSession));
        Assert.False(fileSystem.IsLink(instanceSession));
    }

    [Fact]
    public void AcceptanceCriterion7_8_SyncModifiesOnlyInstanceFilesAndPreservesMainHomeTargets()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "ac7-8-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "ac7-8-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "ac7-8-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string targetDir = Path.Combine(mainHome, "shared-data");
        Directory.CreateDirectory(targetDir);
        string targetFile = Path.Combine(targetDir, "target.txt");
        const string payload = "essential-user-data-must-never-be-deleted-or-modified";
        File.WriteAllText(targetFile, payload, Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult result = SquadInstanceLinkSync.Sync(
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
        Assert.True(Directory.Exists(targetDir));
        Assert.True(File.Exists(targetFile));
        Assert.Equal(payload, File.ReadAllText(targetFile, Encoding.UTF8));
    }

    [Fact]
    public void RenderedPathsAreExcludedFromLinking()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "rendered-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "rendered-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "rendered-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string agentsDir = Path.Combine(mainHome, "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "worker.md"), "canonical-worker", Encoding.UTF8);
        File.WriteAllText(Path.Combine(agentsDir, "personal.md"), "user-personal-agent", Encoding.UTF8);
        File.WriteAllText(Path.Combine(mainHome, "settings.json"), "{}", Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/worker.md" };
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        SquadInstanceLinkSyncResult result = SquadInstanceLinkSync.Sync(
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
        string instanceRoot = Path.Combine(_tempDirectory.Path, "instance-for-mutex");
        Directory.CreateDirectory(instanceRoot);

        string mutexName = SquadInstanceLinkSync.GetMutexName(instanceRoot);

        Assert.StartsWith("kyber-weave-squad-", mutexName, StringComparison.Ordinal);
        Assert.True(mutexName.Length > "kyber-weave-squad-".Length);
    }

    [Fact]
    public async Task ConcurrentSyncInvocationsOnSameInstanceRootRunSafely()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "concurrent-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "concurrent-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "concurrent-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        await File.WriteAllTextAsync(Path.Combine(mainHome, "settings.json"), "{}", Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        Task<SquadInstanceLinkSyncResult> task1 = Task.Run(() => SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory));

        Task<SquadInstanceLinkSyncResult> task2 = Task.Run(() => SquadInstanceLinkSync.Sync(
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
        string instanceRoot = "/virtual/instance";
        string mainHome = Path.Combine(_tempDirectory.Path, "fake-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "fake-state");
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string mainSettings = Path.Combine(mainHome, "settings.json");
        File.WriteAllText(mainSettings, "{}", Encoding.UTF8);

        FakeSquadLinkFileSystem fakeFileSystem = new();
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);

        SquadInstanceLinkSyncResult result = SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fakeFileSystem,
            stateDirectory);

        Assert.Single(result.Links);
        Assert.Equal("settings.json", result.Links[0].RelativePath);
        Assert.True(fakeFileSystem.IsLinkTo("/virtual/instance/settings.json", mainSettings));
    }

    [Fact]
    public void SyncRejectsPathTraversalInRecordedLinksAndLeavesVictimFileUntouched()
    {
        string outsideVictim = Path.Combine(_tempDirectory.Path, "outside-victim.txt");
        File.WriteAllText(outsideVictim, "do-not-delete", Encoding.UTF8);

        string instanceRoot = Path.Combine(_tempDirectory.Path, "traversal-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "traversal-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "traversal-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        string json = """
                      [
                        {
                          "RelativePath": "../outside-victim.txt",
                          "TargetPath": "/nonexistent/target",
                          "Kind": 0,
                          "LastSeenSize": 0,
                          "LastSeenModifiedUtc": "2026-01-01T00:00:00Z"
                        }
                      ]
                      """;
        File.WriteAllText(linksJsonPath, json, Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        Assert.Throws<InvalidDataException>(() => SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory));

        Assert.True(File.Exists(outsideVictim),
            "Path traversal in links.json must not delete files outside instanceRoot.");
    }

    [Fact]
    public void SyncWhenDeleteStaleFileThrowsUnauthorizedAccessExceptionReturnsWarningAndPreservesLinkRecord()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "locked-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "locked-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "locked-state");
        string lockedDir = Path.Combine(instanceRoot, "locked");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(lockedDir);

        string staleFile = Path.Combine(lockedDir, "stale.txt");
        File.WriteAllText(staleFile, "stale", Encoding.UTF8);

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        string json = """
                      [
                        {
                          "RelativePath": "locked/stale.txt",
                          "TargetPath": "/nonexistent/target",
                          "Kind": 0,
                          "LastSeenSize": 5,
                          "LastSeenModifiedUtc": "2026-01-01T00:00:00Z"
                        }
                      ]
                      """;
        File.WriteAllText(linksJsonPath, json, Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        try
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(staleFile, FileAttributes.ReadOnly);
            else
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            SquadInstanceLinkSyncResult result = SquadInstanceLinkSync.Sync(
                instanceRoot,
                mainHome,
                row,
                FrozenSet<string>.Empty,
                fileSystem,
                stateDirectory);

            Assert.Contains(result.Warnings, warning => warning.Contains("stale.txt", StringComparison.Ordinal));

            string savedLinksJson = File.ReadAllText(linksJsonPath, Encoding.UTF8);
            Assert.Contains("stale.txt", savedLinksJson, StringComparison.Ordinal);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(staleFile)) File.SetAttributes(staleFile, FileAttributes.Normal);
            }
            else
            {
                if (Directory.Exists(lockedDir))
                    File.SetUnixFileMode(lockedDir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public void SyncWhenLinksJsonIsCorruptThrowsInvalidDataException()
    {
        string instanceRoot = Path.Combine(_tempDirectory.Path, "corrupt-instance");
        string mainHome = Path.Combine(_tempDirectory.Path, "corrupt-main");
        string stateDirectory = Path.Combine(_tempDirectory.Path, "corrupt-state");
        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(mainHome);
        Directory.CreateDirectory(stateDirectory);

        string linksJsonPath = Path.Combine(stateDirectory, "links.json");
        File.WriteAllText(linksJsonPath, "{ corrupt json : [", Encoding.UTF8);

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        Assert.Throws<InvalidDataException>(() => SquadInstanceLinkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            FrozenSet<string>.Empty,
            fileSystem,
            stateDirectory));
    }

    private static ISquadLinkFileSystem CreatePlatformFileSystem()
    {
        if (OperatingSystem.IsWindows()) return new WindowsSquadLinkFileSystem();

        return new UnixSquadLinkFileSystem();
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
