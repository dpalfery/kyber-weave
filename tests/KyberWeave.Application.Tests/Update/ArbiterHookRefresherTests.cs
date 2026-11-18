using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Update;

/// <summary>
/// The Arbiter hook refresh after an update, over the configuration, lifecycle, state and file-system fakes:
/// which projects it re-renders, what it tells the operator, and that a failure is counted rather than thrown.
/// </summary>
/// <remarks>
/// Nothing here touches the disk or the host's environment. A project root is a path in the fake tree, so the
/// refresh reads its configuration and rewrites nothing unless the scripted lifecycle says it did.
/// </remarks>
public sealed class ArbiterHookRefresherTests
{
    private const string EnabledProject = "/kw/enabled";
    private const string DisabledProject = "/kw/disabled";

    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly FakeSquadStateStore _state = new(new FakeSquadUserPaths("/kw/home/state"));
    private readonly FakeSquadLifecycleService _lifecycle;
    private readonly InMemoryFileSystem _files = new();
    private readonly List<string> _log = [];

    public ArbiterHookRefresherTests()
    {
        _lifecycle = new FakeSquadLifecycleService(_state);
        _configs.WriteRepositoryConfig(EnabledProject, "arbiter:\n  enabled: true\n  provider:\n    kind: none\n");
        _configs.WriteRepositoryConfig(DisabledProject, "arbiter:\n  enabled: false\n  provider:\n    kind: none\n");
    }

    private ArbiterHookRefresher Refresher() => new(
        new FakeProcessEnvironment(homeDirectory: "/kw/home", readVariable: _ => null),
        new NoUserConfig(),
        _configs,
        _lifecycle,
        _state,
        _files,
        new FakeMcpWiringService(),
        _log.Add);

    [Fact]
    public void OnlyAProjectThatEnablesTheArbiterIsRefreshed()
    {
        ArbiterHookRefreshResult result = Refresher().Refresh([EnabledProject, DisabledProject]);

        SquadUpdateRequest update = Assert.Single(_lifecycle.Updates);
        Assert.Equal(EnabledProject, update.TargetRoot);
        Assert.Equal(1, result.Refreshed);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void ARefreshTheLifecycleReportsAsFailedIsCountedAndLogged()
    {
        _lifecycle.Result = new SquadLifecycleResult(false, Errors: ["the renderer refused"]);

        ArbiterHookRefreshResult result = Refresher().Refresh([EnabledProject]);

        Assert.Equal(0, result.Refreshed);
        Assert.Equal(1, result.Failed);
        Assert.Contains(_log, line => line.Contains("the renderer refused", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefreshTheLifecycleThrowsFromIsCountedAndLogged()
    {
        ArbiterHookRefresher refresher = new(
            new FakeProcessEnvironment(homeDirectory: "/kw/home", readVariable: _ => null),
            new NoUserConfig(),
            _configs,
            new ThrowingLifecycle(_state, new InvalidOperationException("the renderer crashed")),
            _state,
            _files,
            new FakeMcpWiringService(),
            _log.Add);

        ArbiterHookRefreshResult result = refresher.Refresh([EnabledProject]);

        Assert.Equal(0, result.Refreshed);
        Assert.Equal(1, result.Failed);
        Assert.Contains(_log, line => line.Contains("the renderer crashed", StringComparison.Ordinal));
    }

    [Fact]
    public void AProjectRootTheConfigRepositoryCannotReadIsSkippedWithAWarning()
    {
        // A name no file system can take: the repository reports it as a file failure, the same answer it gives a
        // project whose configuration cannot be read.
        ArbiterHookRefreshResult result = Refresher().Refresh(["a\0b"]);

        Assert.Empty(_lifecycle.Updates);
        Assert.Equal(1, result.Failed);
        Assert.Contains(_log, line => line.Contains("skipping arbiter hook refresh", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyRootAndAWhitespaceRootAreSkipped()
    {
        ArbiterHookRefreshResult result = Refresher().Refresh([string.Empty, " "]);

        Assert.Empty(_lifecycle.Updates);
        Assert.Equal((0, 0), (result.Refreshed, result.Failed));
    }

    private sealed class NoUserConfig : IUserHostConfigStore
    {
        public string EnsureUserConfig(string home) => string.Empty;

        public void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
        {
        }

        public void RemoveGlobalTarget(string home, string target)
        {
        }

        public IReadOnlyList<string> ReadGlobalTargets(string home) => [];
    }

    /// <summary>A lifecycle whose every call throws, so the refresh's fault answer is what the test sees.</summary>
    private sealed class ThrowingLifecycle(ISquadStateStore state, Exception failure) : ISquadLifecycleService
    {
        public ISquadStateStore StateStore { get; } = state;

        public IReadOnlyList<SquadTarget> EffectiveInstallTargets(SquadInstallRequest request) => throw failure;

        public Task<SquadLifecycleResult> InstallAsync(SquadInstallRequest request, CancellationToken cancellationToken = default) =>
            throw failure;

        public Task<SquadLifecycleResult> UpdateAsync(SquadUpdateRequest request, CancellationToken cancellationToken = default) =>
            throw failure;

        public Task<SquadLifecycleResult> UninstallAsync(SquadUninstallRequest request, CancellationToken cancellationToken = default) =>
            throw failure;
    }
}
