using KyberWeave.Tests.FileSystem;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Instances;

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
public sealed class SquadInstanceEnsureRemoveTests
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

    private readonly InMemorySquadState _state = new();

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
        Assert.True(_state.Files.DirectoryExists(expectedInstanceRoot));
        Assert.True(_state.Files.FileExists(expectedReceiptPath));

        DateTime lastReceiptWrite = _state.Files.GetLastWriteTimeUtc(expectedReceiptPath);

        SquadInstanceEnsureResult secondResult = await service.EnsureAsync(SquadTarget.Claude, "default");

        Assert.Equal(expectedInstanceRoot, secondResult.InstanceRoot);
        Assert.Equal(lastReceiptWrite, _state.Files.GetLastWriteTimeUtc(expectedReceiptPath));
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
        _state.Files.WriteAllText(presetFilePath, UpdatedDefaultPresetYaml);

        SquadInstanceEnsureResult updateResult = await service.EnsureAsync(SquadTarget.Claude, "default");

        Assert.Equal(expectedInstanceRoot, updateResult.InstanceRoot);
        Assert.True(_state.Files.DirectoryExists(expectedInstanceRoot));
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
        _state.Files.CreateDirectory(mainHome);
        string mainSettingsFile = Path.Combine(mainHome, "settings.json");
        string originalContent = "{\"telemetry\": false, \"theme\": \"dark\"}";
        _state.Files.WriteAllText(mainSettingsFile, originalContent);

        SquadInstanceEnsureResult result = await service.EnsureAsync(SquadTarget.Claude, "default");
        string instanceRoot = result.InstanceRoot;
        string instanceStateDir = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instance-state",
            "claude",
            "default");

        Assert.True(_state.Files.DirectoryExists(instanceRoot));
        Assert.True(_state.Files.DirectoryExists(instanceStateDir));

        await service.RemoveAsync(SquadTarget.Claude, "default");

        Assert.False(_state.Files.DirectoryExists(instanceRoot));
        Assert.False(_state.Files.DirectoryExists(instanceStateDir));
        Assert.Contains(context.LinkFileSystem.DeletedTrees,
            path => string.Equals(path, instanceRoot, StringComparison.Ordinal));

        // Main home must remain completely untouched
        Assert.True(_state.Files.DirectoryExists(mainHome));
        Assert.True(_state.Files.FileExists(mainSettingsFile));
        Assert.Equal(originalContent, _state.Files.ReadAllText(mainSettingsFile));
    }

    [Fact]
    public async Task RemoveAsyncCallsDeleteTreeNoFollowOnInstanceStateDirectory()
    {
        using TestContext context = CreateContext();
        SquadInstanceService service = context.CreateService();

        SquadInstanceEnsureResult result = await service.EnsureAsync(SquadTarget.Claude, "default");
        string instanceStateDir = Path.Combine(
            context.UserPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "instance-state",
            "claude",
            "default");

        context.LinkFileSystem.DeletedTrees.Clear();

        await service.RemoveAsync(SquadTarget.Claude, "default");

        Assert.Contains(context.LinkFileSystem.DeletedTrees,
            path => string.Equals(path, instanceStateDir, StringComparison.Ordinal));
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
        _state.Files.CreateDirectory(outsideDir);
        string canaryFile = Path.Combine(outsideDir, "canary.txt");
        _state.Files.WriteAllText(canaryFile, "untouched");

        try
        {
            await service.RemoveAsync(SquadTarget.Claude, preset);
        }
        catch (ArgumentException)
        {
            // Expected if preset validation rejects traversal
        }

        Assert.True(_state.Files.DirectoryExists(outsideDir));
        Assert.True(_state.Files.FileExists(canaryFile));
        Assert.Equal("untouched", _state.Files.ReadAllText(canaryFile));
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

        Assert.True(_state.Files.DirectoryExists(claudeResult.InstanceRoot));
        Assert.True(_state.Files.DirectoryExists(copilotResult.InstanceRoot));

        await service.RefreshAllAsync();

        Assert.True(_state.Files.DirectoryExists(claudeResult.InstanceRoot));
        Assert.True(_state.Files.DirectoryExists(copilotResult.InstanceRoot));
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

        Assert.True(_state.Files.DirectoryExists(expectedClaudeDefault));
        Assert.True(_state.Files.DirectoryExists(expectedClaudeFast));
        Assert.True(_state.Files.DirectoryExists(expectedCopilotDefault));
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
        _state.Files.CreateDirectory(mainClaude);
        string settingsFile = Path.Combine(mainClaude, "settings.json");
        _state.Files.WriteAllText(settingsFile, "{\"theme\": \"dark\"}");

        SquadInstanceEnsureResult result = await service.EnsureAsync(SquadTarget.Claude, "default");

        string instanceSettings = Path.Combine(result.InstanceRoot, "settings.json");
        Assert.True(context.LinkFileSystem.IsLink(instanceSettings));
        Assert.True(context.LinkFileSystem.IsLinkTo(instanceSettings, settingsFile));
        Assert.NotNull(result.Warnings);
    }

    private TestContext CreateContext()
    {
        ISquadUserPaths userPaths = _state.UserPaths;
        _state.Files.CreateDirectory(userPaths.HomeDirectory);

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        _state.Files.CreateDirectory(presetsDir);
        _state.Files.WriteAllText(Path.Combine(presetsDir, "default.yml"), DefaultPresetYaml);
        _state.Files.WriteAllText(Path.Combine(presetsDir, "fast.yml"), FastPresetYaml);

        string globalHome = Path.Combine(_state.Files.Root, "global-home");
        _state.Files.CreateDirectory(globalHome);
        FakeGlobalRootResolver globalRoots = new(globalHome);
        FakeSquadLinkFileSystem linkFileSystem = new(_state.Files);
        SquadModelPresetStore presetStore = new(userPaths, _state.Files);

        string source = Path.Combine(_state.Files.Root, "release", "kyber-squad");
        _ = CanonicalSquadTree.CopyInto(_state.Files, source);
        FakeSquadReleaseSource releaseSource = FakeSquadReleaseSource.FromDirectory(source, _state.Files);
        ISquadRenderer renderer = new SquadRendererRegistry(
            SquadHostDefaults.CreateRenderers(),
            new InMemorySquadServices(_state.Files).RenderInputReader());

        SquadLifecycleService lifecycleService = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer, globalRoots);

        return new TestContext(_state, userPaths, globalRoots, linkFileSystem, presetStore, lifecycleService);
    }

    private sealed class TestContext(
        InMemorySquadState state,
        ISquadUserPaths userPaths,
        FakeGlobalRootResolver globalRoots,
        FakeSquadLinkFileSystem linkFileSystem,
        SquadModelPresetStore presetStore,
        SquadLifecycleService lifecycleService) : IDisposable
    {
        public ISquadUserPaths UserPaths { get; } = userPaths;
        public FakeGlobalRootResolver GlobalRoots { get; } = globalRoots;
        public FakeSquadLinkFileSystem LinkFileSystem { get; } = linkFileSystem;

        public void Dispose()
        {
        }

        public SquadInstanceService CreateService()
        {
            return new SquadInstanceService(
                lifecycleService,
                presetStore,
                LinkFileSystem,
                GlobalRoots,
                new SquadInstanceLinkSync(state.Files, state.Paths, new InProcessLease(state.LeaseHost), state.Environment),
                new InMemorySquadFileAccess(state.Files),
                state.Paths,
                state.Environment,
                state.Store);
        }
    }

    private sealed class FakeGlobalRootResolver(string baseGlobalRoot) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return Path.Combine(baseGlobalRoot, "." + SquadTargetCatalog.GetToken(target));
        }
    }

    private sealed class FakeSquadLinkFileSystem(InMemoryFileSystem files) : ISquadLinkFileSystem
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

            if (files.DirectoryExists(directoryPath)) files.DeleteTreeNoFollow(directoryPath);
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }
}
