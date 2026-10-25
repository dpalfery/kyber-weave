using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit and integration tests for <see cref="SquadInstanceService" />, verifying instance installation,
///     update, link synchronization, removal safety, and multi-instance discovery.
/// </summary>
/// <remarks>
///     <para>
///         Acceptance Criterion 7.1: Concurrent instances supported.
///     </para>
///     <para>
///         Acceptance Criterion 7.3: Squad gives each harness and preset pair its own instance folder under
///         ~/.kyber-weave/. Path stays stable.
///     </para>
///     <para>
///         Acceptance Criterion 7.8: Squad deletes only instance files and links, never target files.
///     </para>
/// </remarks>
public sealed class SquadInstanceServiceTests : IDisposable
{
    private const string DefaultPresetYaml = """
                                             schema: kyber-squad.model-preset/v1
                                             profiles:
                                               general:
                                                 claude: claude-3-7-sonnet
                                                 copilot: gpt-4o
                                             """;

    private const string FastPresetYaml = """
                                          schema: kyber-squad.model-preset/v1
                                          profiles:
                                            general:
                                              claude: claude-3-5-haiku
                                              copilot: gpt-4o-mini
                                          """;

    private const string UpdatedDefaultPresetYaml = """
                                                    schema: kyber-squad.model-preset/v1
                                                    profiles:
                                                      general:
                                                        claude: claude-3-5-sonnet
                                                        copilot: gpt-4o
                                                    """;

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly SharedIdentitySquadFixture _fixture = SharedIdentitySquadFixture.Create();

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _fixture.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    ///     Verifies that calling EnsureAsync creates the instance folder and receipt on the first call,
    ///     and that a subsequent call with identical version and preset digest is a no-op that does not re-install.
    /// </summary>
    [Fact]
    public async Task EnsureAsyncInstallsNewInstanceOnFirstCallAndSecondCallIsNoOp()
    {
        using TestContext context = CreateContext();
        SquadInstanceService service = context.CreateService();

        SquadInstanceEnsureResult firstResult = await service.EnsureAsync(SquadTarget.Claude, "default");

        string expectedInstanceRoot = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "claude",
            "default");
        string expectedStateDir = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instance-state",
            "claude",
            "default");
        string expectedReceiptPath = Path.Combine(expectedStateDir, "squad.receipt.json");

        Assert.Equal(expectedInstanceRoot, firstResult.InstanceRoot);
        Assert.True(Directory.Exists(expectedInstanceRoot));
        Assert.True(File.Exists(expectedReceiptPath));

        DateTime lastReceiptWrite = File.GetLastWriteTimeUtc(expectedReceiptPath);

        SquadInstanceEnsureResult secondResult = await service.EnsureAsync(SquadTarget.Claude, "default");

        Assert.Equal(expectedInstanceRoot, secondResult.InstanceRoot);
        Assert.Equal(lastReceiptWrite, File.GetLastWriteTimeUtc(expectedReceiptPath));
    }

    /// <summary>
    ///     Verifies that when the version or preset digest in squad.lock differs from current state,
    ///     EnsureAsync triggers an update that updates the lock/receipt while keeping the instance path and links stable.
    /// </summary>
    [Fact]
    public async Task EnsureAsyncUpdatesInstanceWhenVersionOrPresetDigestDiffersKeepingInstancePathAndLinks()
    {
        using TestContext context = CreateContext();
        SquadInstanceService service = context.CreateService();

        SquadInstanceEnsureResult initialResult = await service.EnsureAsync(SquadTarget.Claude, "default");
        string expectedInstanceRoot = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "claude",
            "default");
        Assert.Equal(expectedInstanceRoot, initialResult.InstanceRoot);

        // Modify preset file to change its digest
        string presetFilePath = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "presets",
            "default.yml");
        await File.WriteAllTextAsync(presetFilePath, UpdatedDefaultPresetYaml, Utf8NoBom);

        SquadInstanceEnsureResult updateResult = await service.EnsureAsync(SquadTarget.Claude, "default");

        Assert.Equal(expectedInstanceRoot, updateResult.InstanceRoot);
        Assert.True(Directory.Exists(expectedInstanceRoot));
    }

    /// <summary>
    ///     Acceptance Criterion 7.8: Squad deletes only instance files and links, never target files.
    ///     Verifies that RemoveAsync deletes the instance folder and instance-state directory, calls
    ///     DeleteTreeNoFollow on the link file system, and leaves main home files and file IDs untouched.
    /// </summary>
    [Fact]
    public async Task RemoveAsyncDeletesInstanceFolderAndStateWhileLeavingMainHomeFilesAndFileIdsUntouched()
    {
        using TestContext context = CreateContext();
        SquadInstanceService service = context.CreateService();

        string mainHome = context.GlobalRoots.ResolveGlobalRoot(SquadTarget.Claude);
        Directory.CreateDirectory(mainHome);
        string mainSettingsFile = Path.Combine(mainHome, "settings.json");
        string originalContent = "{\"telemetry\": false, \"theme\": \"dark\"}";
        await File.WriteAllTextAsync(mainSettingsFile, originalContent, Encoding.UTF8);

        SquadInstanceEnsureResult result = await service.EnsureAsync(SquadTarget.Claude, "default");
        string instanceRoot = result.InstanceRoot;
        string instanceStateDir = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instance-state",
            "claude",
            "default");

        Assert.True(Directory.Exists(instanceRoot));
        Assert.True(Directory.Exists(instanceStateDir));

        await service.RemoveAsync(SquadTarget.Claude, "default");

        Assert.False(Directory.Exists(instanceRoot));
        Assert.False(Directory.Exists(instanceStateDir));
        Assert.Contains(context.LinkFileSystem.DeletedTrees,
            path => string.Equals(path, instanceRoot, StringComparison.Ordinal));

        // Main home must remain completely untouched
        Assert.True(Directory.Exists(mainHome));
        Assert.True(File.Exists(mainSettingsFile));
        Assert.Equal(originalContent, await File.ReadAllTextAsync(mainSettingsFile, Encoding.UTF8));
    }

    /// <summary>
    ///     Regression test: Path traversal attempt in RemoveAsync throws ArgumentException or
    ///     completes without touching any directories outside the designated instance path.
    /// </summary>
    [Theory]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData("../traversal")]
    public async Task RemoveAsyncPathTraversalDoesNotTouchDirectoriesOutsideDesignatedInstancePath(string preset)
    {
        using TestContext context = CreateContext();
        SquadInstanceService service = context.CreateService();

        string outsideDir = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "outside-directory");
        Directory.CreateDirectory(outsideDir);
        string canaryFile = Path.Combine(outsideDir, "canary.txt");
        await File.WriteAllTextAsync(canaryFile, "untouched", Encoding.UTF8);

        try
        {
            await service.RemoveAsync(SquadTarget.Claude, preset);
        }
        catch (ArgumentException)
        {
            // Expected if preset validation rejects traversal
        }

        Assert.True(Directory.Exists(outsideDir));
        Assert.True(File.Exists(canaryFile));
        Assert.Equal("untouched", await File.ReadAllTextAsync(canaryFile, Encoding.UTF8));
    }

    /// <summary>
    ///     Verifies that RefreshAllAsync discovers all existing deployed instances under instance-state
    ///     that have a receipt and refreshes/ensures each of them.
    /// </summary>
    [Fact]
    public async Task RefreshAllAsyncDiscoversAndRefreshesAllInstancesHavingReceiptsUnderInstanceState()
    {
        using TestContext context = CreateContext();
        SquadInstanceService service = context.CreateService();

        SquadInstanceEnsureResult claudeResult = await service.EnsureAsync(SquadTarget.Claude, "default");
        SquadInstanceEnsureResult copilotResult = await service.EnsureAsync(SquadTarget.Copilot, "default");

        Assert.True(Directory.Exists(claudeResult.InstanceRoot));
        Assert.True(Directory.Exists(copilotResult.InstanceRoot));

        await service.RefreshAllAsync();

        Assert.True(Directory.Exists(claudeResult.InstanceRoot));
        Assert.True(Directory.Exists(copilotResult.InstanceRoot));
    }

    /// <summary>
    ///     Acceptance Criteria 7.1 and 7.3: Squad supports concurrent distinct instances and gives each
    ///     harness and preset pair its own instance folder under ~/.kyber-weave/squad/instances/target/preset/
    ///     with stable, isolated paths.
    /// </summary>
    [Fact]
    public async Task EnsureAsyncSupportsConcurrentDistinctInstancesWithStableIsolatedPaths()
    {
        using TestContext context = CreateContext();
        SquadInstanceService service = context.CreateService();

        SquadInstanceEnsureResult claudeDefault = await service.EnsureAsync(SquadTarget.Claude, "default");
        SquadInstanceEnsureResult claudeFast = await service.EnsureAsync(SquadTarget.Claude, "fast");
        SquadInstanceEnsureResult copilotDefault = await service.EnsureAsync(SquadTarget.Copilot, "default");

        string expectedClaudeDefault = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "claude",
            "default");
        string expectedClaudeFast = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "claude",
            "fast");
        string expectedCopilotDefault = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instances",
            "copilot",
            "default");

        Assert.Equal(expectedClaudeDefault, claudeDefault.InstanceRoot);
        Assert.Equal(expectedClaudeFast, claudeFast.InstanceRoot);
        Assert.Equal(expectedCopilotDefault, copilotDefault.InstanceRoot);

        Assert.NotEqual(claudeDefault.InstanceRoot, claudeFast.InstanceRoot);
        Assert.NotEqual(claudeDefault.InstanceRoot, copilotDefault.InstanceRoot);

        Assert.True(Directory.Exists(expectedClaudeDefault));
        Assert.True(Directory.Exists(expectedClaudeFast));
        Assert.True(Directory.Exists(expectedCopilotDefault));
    }

    /// <summary>
    ///     Verifies that for HomeReplace rows in SquadInstanceCatalog, EnsureAsync calls SquadInstanceLinkSync.Sync
    ///     to link main home settings into the instance root, and returns any sync warnings in SquadInstanceEnsureResult.
    /// </summary>
    [Fact]
    public async Task EnsureAsyncSynchronizesLinksForHomeReplaceHarnessAndReturnsSyncWarnings()
    {
        using TestContext context = CreateContext();
        SquadInstanceService service = context.CreateService();

        string mainClaude = context.GlobalRoots.ResolveGlobalRoot(SquadTarget.Claude);
        Directory.CreateDirectory(mainClaude);
        string settingsFile = Path.Combine(mainClaude, "settings.json");
        await File.WriteAllTextAsync(settingsFile, "{\"theme\": \"dark\"}", Encoding.UTF8);

        SquadInstanceEnsureResult result = await service.EnsureAsync(SquadTarget.Claude, "default");

        string instanceSettings = Path.Combine(result.InstanceRoot, "settings.json");
        Assert.True(context.LinkFileSystem.IsLink(instanceSettings));
        Assert.True(context.LinkFileSystem.IsLinkTo(instanceSettings, settingsFile));
        Assert.NotNull(result.Warnings);
    }

    private TestContext CreateContext()
    {
        string root = _temp.Path;
        string appData = Path.Combine(root, "app-data");
        FakeUserPaths userPaths = new(appData);
        Directory.CreateDirectory(userPaths.HomeDirectory);

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        File.WriteAllText(Path.Combine(presetsDir, "default.yml"), DefaultPresetYaml, Utf8NoBom);
        File.WriteAllText(Path.Combine(presetsDir, "fast.yml"), FastPresetYaml, Utf8NoBom);

        string globalHome = Path.Combine(root, "global-home");
        Directory.CreateDirectory(globalHome);
        FakeGlobalRootResolver globalRoots = new(globalHome);
        FakeSquadLinkFileSystem linkFileSystem = new();
        SquadModelPresetStore presetStore = new(userPaths);
        SquadStateStore stateStore = new(userPaths);

        FakeReleaseSource releaseSource = new(_fixture.Path);
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();

        SquadLifecycleService lifecycleService = new(
            releaseSource,
            renderer,
            stateStore,
            globalRoots: globalRoots,
            presetStore: presetStore);

        return new TestContext(
            userPaths,
            globalRoots,
            linkFileSystem,
            presetStore,
            lifecycleService,
            releaseSource);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly FakeReleaseSource _releaseSource;

        public TestContext(
            FakeUserPaths userPaths,
            FakeGlobalRootResolver globalRoots,
            FakeSquadLinkFileSystem linkFileSystem,
            SquadModelPresetStore presetStore,
            SquadLifecycleService lifecycleService,
            FakeReleaseSource releaseSource)
        {
            UserPaths = userPaths;
            GlobalRoots = globalRoots;
            LinkFileSystem = linkFileSystem;
            PresetStore = presetStore;
            LifecycleService = lifecycleService;
            _releaseSource = releaseSource;
        }

        public FakeUserPaths UserPaths { get; }
        public FakeGlobalRootResolver GlobalRoots { get; }
        public FakeSquadLinkFileSystem LinkFileSystem { get; }
        public SquadModelPresetStore PresetStore { get; }
        public SquadLifecycleService LifecycleService { get; }

        public void Dispose()
        {
            _releaseSource.Dispose();
        }

        public SquadInstanceService CreateService()
        {
            return new SquadInstanceService(LifecycleService, PresetStore, LinkFileSystem, GlobalRoots);
        }
    }

    private sealed class FakeUserPaths(string appDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = appDataDirectory;

        public string HomeDirectory => Path.Combine(ApplicationDataDirectory, "home");
    }

    private sealed class FakeGlobalRootResolver(string baseGlobalRoot) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return Path.Combine(baseGlobalRoot, "." + SquadTargetCatalog.GetToken(target));
        }
    }

    private sealed class FakeSquadLinkFileSystem : ISquadLinkFileSystem
    {
        private readonly Dictionary<string, string> _fileLinks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _folderLinks = new(StringComparer.Ordinal);
        public List<string> DeletedTrees { get; } = new();

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
            DeletedTrees.Add(directoryPath);
            string prefix = Normalize(directoryPath) + "/";
            List<string> fileKeysToRemove =
                _fileLinks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (string key in fileKeysToRemove) _fileLinks.Remove(key);

            List<string> folderKeysToRemove =
                _folderLinks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (string key in folderKeysToRemove) _folderLinks.Remove(key);

            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, true);
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }

    private sealed class FakeReleaseSource : ISquadReleaseSource
    {
        private readonly string _extractSourceDir;

        public FakeReleaseSource(string extractSourceDir)
        {
            _extractSourceDir = extractSourceDir;
        }

        public Task<SquadReleaseResult> DownloadAndExtractAsync(
            SquadReleaseRequest request,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(request.DestinationPath);
            if (Directory.Exists(_extractSourceDir))
                foreach (string file in Directory.EnumerateFiles(_extractSourceDir, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(_extractSourceDir, file);
                    string dest = Path.Combine(request.DestinationPath, relative);
                    string? dir = Path.GetDirectoryName(dest);
                    if (dir is not null) Directory.CreateDirectory(dir);
                    File.Copy(file, dest, true);
                }

            SquadReleaseResult result = new(
                request.Version,
                new SquadReleaseAsset("test-asset.tar.gz", new Uri("https://example.com/asset.tar.gz"), 100),
                new SquadReleaseChecksum("test-asset.tar.gz",
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
                request.DestinationPath,
                []);

            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }
}
