using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Instances;

/// <summary>The instance service installing a new instance under the home directory, through a fake path seam and fake collaborators.</summary>
public sealed class SquadInstanceServiceTests
{
    private const string Home = "/kw/home";

    private readonly RecordingLifecycle _lifecycle = new();
    private readonly SquadInstanceService _service;

    public SquadInstanceServiceTests() =>
        _service = new SquadInstanceService(
            _lifecycle,
            new NoPresets(),
            new NoLinks(),
            new NoGlobalRoots(),
            new NoLinkSync(),
            new NoFiles(),
            new FakePathResolver(new InMemoryFileSystem()),
            new FakeProcessEnvironment(currentDirectory: "/kw/work"),
            new HomeOnlyStateStore(new FakeSquadUserPaths("/kw/data", Home)));

    [Fact]
    public async Task ANewInstanceIsInstalledUnderTheHomeInstancesFolder()
    {
        SquadInstanceEnsureResult result = await _service.EnsureAsync(SquadTarget.OpenCode, "fast");

        string expected = Path.Combine(Home, ".kyber-weave", "squad", "instances", "opencode", "fast");
        Assert.Equal(expected, result.InstanceRoot);
        Assert.Equal(expected, _lifecycle.Installed?.TargetRoot);
    }

    [Fact]
    public async Task AHomeWrittenWithDotSegmentsStillContainsTheInstance()
    {
        SquadInstanceService service = new(
            _lifecycle,
            new NoPresets(),
            new NoLinks(),
            new NoGlobalRoots(),
            new NoLinkSync(),
            new NoFiles(),
            new FakePathResolver(new InMemoryFileSystem()),
            new FakeProcessEnvironment(currentDirectory: "/kw/work"),
            new HomeOnlyStateStore(new FakeSquadUserPaths("/kw/data", "/kw/other/../home")));

        SquadInstanceEnsureResult result = await service.EnsureAsync(SquadTarget.OpenCode, "fast");

        Assert.NotNull(_lifecycle.Installed);
        Assert.EndsWith("opencode/fast", result.InstanceRoot.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APresetNameThatIsNotAWordIsRefused()
    {
        _ = await Assert.ThrowsAsync<ArgumentException>(() => _service.EnsureAsync(SquadTarget.OpenCode, "../x"));
    }

    private sealed class RecordingLifecycle : ISquadLifecycleService
    {
        public SquadInstallRequest? Installed { get; private set; }

        public ISquadStateStore StateStore => throw new NotSupportedException();

        public IReadOnlyList<SquadTarget> EffectiveInstallTargets(SquadInstallRequest request) => throw new NotSupportedException();

        public Task<SquadLifecycleResult> InstallAsync(SquadInstallRequest request, CancellationToken cancellationToken = default)
        {
            Installed = request;
            return Task.FromResult(new SquadLifecycleResult(true));
        }

        public Task<SquadLifecycleResult> UpdateAsync(SquadUpdateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SquadLifecycleResult> UninstallAsync(SquadUninstallRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoPresets : ISquadModelPresetStore
    {
        public IReadOnlyList<string> List() => [];

        public SquadModelPreset? TryLoad(string name) => null;
    }

    private sealed class NoLinks : ISquadLinkFileSystem
    {
        public void CreateFileLink(string linkPath, string targetPath) => throw new NotSupportedException();

        public void CreateFolderLink(string linkPath, string targetPath) => throw new NotSupportedException();

        public bool IsLink(string path) => false;

        public bool IsLinkTo(string linkPath, string expectedTarget) => false;

        public void DeleteTreeNoFollow(string directoryPath) => throw new NotSupportedException();
    }

    private sealed class NoGlobalRoots : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => throw new NotSupportedException();
    }

    private sealed class NoLinkSync : ISquadInstanceLinkSync
    {
        public SquadInstanceLinkSyncResult Sync(
            string instanceRoot,
            string mainHome,
            SquadInstanceRow row,
            IReadOnlySet<string> renderedPaths,
            ISquadLinkFileSystem fileSystem,
            string instanceStateDirectory) => throw new NotSupportedException();
    }

    private sealed class NoFiles : ISquadFileAccess
    {
        public bool FileExists(string path) => false;

        public bool DirectoryExists(string path) => false;

        public string ReadAllText(string path) => throw new NotSupportedException();

        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();

        public byte[] ReadBytesUpTo(string path, int maxBytes) => throw new NotSupportedException();

        public long GetLength(string path) => throw new NotSupportedException();

        public void CreateDirectory(string path)
        {
        }

        public void DeleteDirectory(string path) => throw new NotSupportedException();

        public string NewScratchDirectoryPath(string prefix) => throw new NotSupportedException();

        public IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory) => [];

        public string? FindCheckoutSquadSourceRoot() => null;

        public void WriteAllBytesAtomic(string path, byte[] contents) => throw new NotSupportedException();
    }

    private sealed class HomeOnlyStateStore(ISquadUserPaths userPaths) : ISquadStateStore
    {
        public ISquadUserPaths UserPaths { get; } = userPaths;

        public string ResolveStateDirectory(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

        public string ResolveHomeStateDirectory() => throw new NotSupportedException();

        public string ResolveHomeOwnedFilesRoot() => throw new NotSupportedException();

        public IReadOnlyList<SquadProjectRegistryEntry> ListProjects() => throw new NotSupportedException();

        public SquadProjectRegistryEntry? FindProject(string root) => throw new NotSupportedException();

        public bool IsRegistered(string root) => throw new NotSupportedException();

        public IReadOnlyList<SquadProjectRegistryEntry> ListProjectsWithoutRebuild() => throw new NotSupportedException();

        public void RegisterProject(string root, IReadOnlyList<string> targets, DateTimeOffset now) => throw new NotSupportedException();

        public bool UnregisterProject(string root) => throw new NotSupportedException();

        public string SerializeLock(SquadLock squadLock) => throw new NotSupportedException();

        public SquadLock DeserializeLock(string yaml) => throw new NotSupportedException();

        public string SerializeReceipt(SquadReceipt receipt) => throw new NotSupportedException();

        public SquadReceipt DeserializeReceipt(string json) => throw new NotSupportedException();

        public SquadReceipt? ReadReceipt(string targetRoot, SquadDeploymentScope scope) => null;

        public SquadLock? ReadLock(string targetRoot, SquadDeploymentScope scope) => null;

        public string ResolveLockPath(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

        public string ResolveReceiptPath(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

        public string ResolveTransactionDirectory(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

        public string ResolveTransactionWorkDirectory(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

        public string ResolveFileWorkDirectory(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

        public string ResolveStateAuthorityRoot(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

        public string ResolveLeaseIdentity(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

        public string ResolveHomeRoot() => throw new NotSupportedException();

        public string ResolveHomeCreationBoundary() => throw new NotSupportedException();
    }
}
