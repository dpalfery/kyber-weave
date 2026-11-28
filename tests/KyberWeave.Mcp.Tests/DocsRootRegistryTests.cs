using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Which worktree a per-call <c>projectPath</c> names, decided from the path, file, process-environment and git seams
/// alone, and how an unreadable host configuration is reported on the console seam.
/// </summary>
public sealed class DocsRootRegistryTests
{
    private static readonly string Server = Absolute("kw", "server");
    private static readonly string Work = Absolute("kw", "work");
    private static readonly string Common = Absolute("kw", "server", ".git");

    private static readonly string RepoA = Absolute("kw", "a");

    private readonly InMemoryFileSystem _files = new();
    private readonly List<string> _built = [];
    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly DocsRootRegistry _placeholder;
    private Action<string>? _onBuild;

    public DocsRootRegistryTests()
    {
        _files.CreateDirectory(RepoA);
        _configs.WriteRepositoryConfig(RepoA, "stop-on-tool-failure: true\n");

        // The tools a scripted factory hands out only need to be distinct, non-null instances; their own registry is
        // never asked to resolve anything.
        _placeholder = DocsRootRegistry.ForAnyRepo(
            _ => throw new InvalidOperationException("the placeholder registry builds nothing"),
            new ScriptedGit(),
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(Work),
            _configs);
    }

    [Fact]
    public async Task RelativeProjectPathIsResolvedAgainstTheProcessWorkingDirectory()
    {
        string worktree = Path.Combine(Work, "wt");
        _files.CreateDirectory(worktree);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(Server, Server, Common).Add(worktree, worktree, Common));

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, "wt");

        Assert.True(resolution.Resolved, resolution.Refusal);
        Assert.Equal([worktree], _built);
    }

    [Fact]
    public async Task ProjectPathThatIsNotADirectoryIsRefusedWithItsFullPath()
    {
        DocsRootRegistry registry = Registry(new ScriptedGit());

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, "missing");

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Tools);
        Assert.Contains($"projectPath '{Path.Combine(Work, "missing")}' is not a directory", resolution.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectPathOfAnotherRepositoryIsRefused()
    {
        string other = Absolute("kw", "other");
        _files.CreateDirectory(other);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(Server, Server, Common).Add(other, other, Absolute("kw", "other", ".git")));

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, other);

        Assert.False(resolution.Resolved);
        Assert.Contains("belongs to a different repository", resolution.Refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public async Task ServerRootThatIsNotARepositoryRefusesEveryProjectPath()
    {
        string worktree = Path.Combine(Work, "wt");
        _files.CreateDirectory(worktree);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(worktree, worktree, Common));

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, worktree);

        Assert.False(resolution.Resolved);
        Assert.Contains("is not a git repository", resolution.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperatingSystemKind.Linux, 1)]
    [InlineData(OperatingSystemKind.MacOS, 0)]
    [InlineData(OperatingSystemKind.Windows, 0)]
    public async Task TopLevelsThatDifferOnlyInCaseAreTheSameRootOnlyWhereTheHostFoldsCase(OperatingSystemKind platform, int toolsBuilt)
    {
        string shouting = Absolute("kw", "SERVER");
        _files.CreateDirectory(shouting);
        DocsRootRegistry registry = Registry(
            new ScriptedGit().Add(Server, Server, Common).Add(shouting, shouting, Common),
            platform);

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, shouting);

        Assert.True(resolution.Resolved, resolution.Refusal);
        Assert.Equal(toolsBuilt, _built.Count);
    }

    [Fact]
    public void UnreadableConfigurationIsReportedOnTheConsoleSeamAndTheDefaultOntologyServes()
    {
        FakeKyberWeaveConfigRepository config = new();
        config.WriteRepositoryConfig(Server, "stop-on-tool-failure: true\n");
        using IDisposable failing = config.FailReads(FakeKyberWeaveConfigRepository.RepositoryConfigPath(Server));
        using FakeConsole console = new();
        ServiceCollection services = new();
        services.AddSingleton<IKyberWeaveConfigRepository>(config);
        services.AddSingleton<IConsole>(console);
        using ServiceProvider provider = services.BuildServiceProvider();

        OntologyConfig ontology = DocsRootRegistry.ResolveOntology(Server, provider);

        Assert.Equal(OntologyConfig.ProductDefaults, ontology);
        Assert.Contains("Failed to load", console.ErrorOutput, StringComparison.Ordinal);
        Assert.Contains("Serving the default ontology", console.ErrorOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, console.Output);
    }

    [Fact]
    public async Task BoundModeAsksGitOnceForARepeatedProjectPath()
    {
        string worktree = Path.Combine(Work, "wt");
        _files.CreateDirectory(worktree);
        ScriptedGit git = new ScriptedGit().Add(Server, Server, Common).Add(worktree, worktree, Common);
        DocsRootRegistry registry = Registry(git);

        for (int call = 0; call < 3; call++)
        {
            DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, worktree);
            Assert.True(resolution.Resolved, resolution.Refusal);
        }

        Assert.Equal(1, git.CallsFor(worktree));
        Assert.Equal([worktree], _built);
    }

    [Fact]
    public async Task AnyRepoPathInsideAnInitializedRepositoryResolvesToItsTopLevel()
    {
        string inside = Path.Combine(RepoA, "src");
        _files.CreateDirectory(inside);
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit().Add(inside, RepoA, Path.Combine(RepoA, ".git")));

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, inside);

        Assert.True(resolution.Resolved, resolution.Refusal);
        Assert.NotNull(resolution.Tools);
        Assert.Equal([RepoA], _built);
    }

    [Fact]
    public async Task AnyRepoPathThatIsNotADirectoryIsRefused()
    {
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit());

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, Path.Combine(Work, "missing"));

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Tools);
        Assert.Contains("is not a directory", resolution.Refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public async Task AnyRepoDirectoryOutsideGitIsRefused()
    {
        string plain = Absolute("kw", "plain");
        _files.CreateDirectory(plain);
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit());

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, plain);

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Tools);
        Assert.Contains("is not inside a git repository", resolution.Refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public async Task AnyRepoGitRepositoryWithoutHostConfigurationIsRefused()
    {
        string bare = Absolute("kw", "bare");
        _files.CreateDirectory(bare);
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit().Add(bare, bare, Path.Combine(bare, ".git")));

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, bare);

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Tools);
        Assert.Contains("has no Kyber-Weave host configuration", resolution.Refusal, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs init", resolution.Refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public async Task AnyRepoBlankProjectPathIsRefused()
    {
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit());

        DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, "  ");

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Tools);
        Assert.Contains("projectPath is required", resolution.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyRepoAsksGitOnceForARepeatedProjectPath()
    {
        ScriptedGit git = new ScriptedGit().Add(RepoA, RepoA, Path.Combine(RepoA, ".git"));
        DocsRootRegistry registry = AnyRegistry(git);

        for (int call = 0; call < 3; call++)
        {
            DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, RepoA);
            Assert.True(resolution.Resolved, resolution.Refusal);
        }

        Assert.Equal(1, git.CallsFor(RepoA));
        Assert.Equal([RepoA], _built);
    }

    [Fact]
    public async Task AnyRepoTwoPathsInOneRepositoryShareOneSetOfTools()
    {
        string inside = Path.Combine(RepoA, "src");
        _files.CreateDirectory(inside);
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit()
            .Add(RepoA, RepoA, Path.Combine(RepoA, ".git"))
            .Add(inside, RepoA, Path.Combine(RepoA, ".git")));

        DocsRootRegistry.DocsRootResolution first = await ResolveAsync(registry, RepoA);
        Assert.True(first.Resolved, first.Refusal);
        DocsRootRegistry.DocsRootResolution second = await ResolveAsync(registry, inside);
        Assert.True(second.Resolved, second.Refusal);

        Assert.Same(first.Tools, second.Tools);
        Assert.Equal([RepoA], _built);
        Assert.Equal(1, registry.Count);
    }

    /// <summary>
    /// The root a fifth one evicts is the one used least recently, not the one built first: the first root is used
    /// again before the fifth arrives, so a first-in-first-out cache would evict it and this one must not.
    /// </summary>
    [Fact]
    public async Task AnyRepoFifthRootEvictsTheLeastRecentlyUsed()
    {
        string[] roots = InitializedRoots(5);
        DocsRootRegistry registry = AnyRegistry(Git(roots));

        foreach (string root in roots[..4])
        {
            DocsRootRegistry.DocsRootResolution held = await ResolveAsync(registry, root);
            Assert.True(held.Resolved, held.Refusal);
        }

        DocsRootRegistry.DocsRootResolution first = await ResolveAsync(registry, roots[0]);
        Assert.True(first.Resolved, first.Refusal);
        DocsRootRegistry.DocsRootResolution fifth = await ResolveAsync(registry, roots[4]);
        Assert.True(fifth.Resolved, fifth.Refusal);
        Assert.Equal(DocsRootRegistry.Capacity, registry.Count);
        Assert.Equal(5, _built.Count);

        DocsRootRegistry.DocsRootResolution firstAgain = await ResolveAsync(registry, roots[0]);
        Assert.True(firstAgain.Resolved, firstAgain.Refusal);
        Assert.Equal(1, _built.Count(root => root == roots[0]));

        DocsRootRegistry.DocsRootResolution second = await ResolveAsync(registry, roots[1]);
        Assert.True(second.Resolved, second.Refusal);
        Assert.Equal(2, _built.Count(root => root == roots[1]));
        Assert.Equal(DocsRootRegistry.Capacity, registry.Count);
    }

    [Fact]
    public async Task AnyRepoFactoryBlockedOnOneRootDoesNotBlockACachedRoot()
    {
        string[] roots = InitializedRoots(2);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        _onBuild = root =>
        {
            if (root != roots[0]) return;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        };
        DocsRootRegistry registry = AnyRegistry(Git(roots));
        DocsRootRegistry.DocsRootResolution cached = await ResolveAsync(registry, roots[1]);
        Assert.True(cached.Resolved, cached.Refusal);

        Task<DocsRootRegistry.DocsRootResolution> slow = Task.Run(() => ResolveAsync(registry, roots[0]));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        DocsRootRegistry.DocsRootResolution again = await ResolveAsync(registry, roots[1]);

        Assert.True(again.Resolved, again.Refusal);
        Assert.Same(cached.Tools, again.Tools);
        Assert.False(slow.IsCompleted);
        release.Set();
        DocsRootRegistry.DocsRootResolution slowResolution = await slow;
        Assert.True(slowResolution.Resolved);
        Assert.NotNull(slowResolution.Tools);
    }

    [Fact]
    public async Task AnyRepoTwoConcurrentFirstCallsForOneRootEndWithOneSharedInstance()
    {
        using Barrier both = new(2);
        _onBuild = _ => Assert.True(both.SignalAndWait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit().Add(RepoA, RepoA, Path.Combine(RepoA, ".git")));

        Task<DocsRootRegistry.DocsRootResolution> first = Task.Run(() => ResolveAsync(registry, RepoA));
        Task<DocsRootRegistry.DocsRootResolution> second = Task.Run(() => ResolveAsync(registry, RepoA));
        DocsRootRegistry.DocsRootResolution[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Resolved));
        Assert.Same(results[0].Tools, results[1].Tools);
        Assert.Equal(2, _built.Count);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task AnyRepoCallHoldingAnEvictedRootFinishesOnItsToolsAndTheNextCallBuildsNew()
    {
        string[] roots = InitializedRoots(5);
        DocsRootRegistry registry = AnyRegistry(Git(roots));
        DocsRootRegistry.DocsRootResolution held = await ResolveAsync(registry, roots[0]);
        Assert.True(held.Resolved, held.Refusal);

        foreach (string other in roots[1..])
        {
            DocsRootRegistry.DocsRootResolution resolution = await ResolveAsync(registry, other);
            Assert.True(resolution.Resolved, resolution.Refusal);
        }

        Assert.Equal(DocsRootRegistry.Capacity, registry.Count);
        Assert.NotNull(held.Tools);
        Assert.Contains("mode=any-repo", await held.Tools.DocsStatusAsync(), StringComparison.Ordinal);

        DocsRootRegistry.DocsRootResolution rebuilt = await ResolveAsync(registry, roots[0]);
        Assert.True(rebuilt.Resolved, rebuilt.Refusal);
        Assert.NotSame(held.Tools, rebuilt.Tools);
        Assert.Equal(2, _built.Count(root => root == roots[0]));
    }

    [Fact]
    public async Task AnyRepoFactoryFailureIsARefusalForThatCallOnlyAndLeavesNoEntry()
    {
        int attempts = 0;
        _onBuild = _ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("the corpus could not be built");
        };
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit().Add(RepoA, RepoA, Path.Combine(RepoA, ".git")));

        DocsRootRegistry.DocsRootResolution failed = await ResolveAsync(registry, RepoA);

        Assert.False(failed.Resolved);
        Assert.Null(failed.Tools);
        Assert.Contains("the corpus could not be built", failed.Refusal, StringComparison.Ordinal);
        Assert.Equal(0, registry.Count);
        DocsRootRegistry.DocsRootResolution resolved = await ResolveAsync(registry, RepoA);
        Assert.True(resolved.Resolved, resolved.Refusal);
        Assert.NotNull(resolved.Tools);
    }

    private static Task<DocsRootRegistry.DocsRootResolution> ResolveAsync(
        DocsRootRegistry registry,
        string? projectPath) =>
        registry.TryResolveAsync(projectPath);

    private string[] InitializedRoots(int count)
    {
        string[] roots = new string[count];
        for (int index = 0; index < count; index++)
        {
            roots[index] = Absolute("kw", "repo" + index);
            _files.CreateDirectory(roots[index]);
            _configs.WriteRepositoryConfig(roots[index], "stop-on-tool-failure: true\n");
        }

        return roots;
    }

    private static ScriptedGit Git(IEnumerable<string> roots)
    {
        ScriptedGit git = new();
        foreach (string root in roots) git.Add(root, root, Path.Combine(root, ".git"));
        return git;
    }

    private DocsRootRegistry AnyRegistry(ScriptedGit git) =>
        DocsRootRegistry.ForAnyRepo(
            root =>
            {
                _onBuild?.Invoke(root);
                lock (_built) _built.Add(root);
                return new DocsTools(_placeholder);
            },
            git,
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(Work),
            _configs);

    private DocsRootRegistry Registry(ScriptedGit git, OperatingSystemKind platform = OperatingSystemKind.Linux) =>
        new(
            Server,
            root =>
            {
                _built.Add(root);
                return null!;
            },
            git,
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(Work, operatingSystem: platform));

    private static string Absolute(params string[] segments) =>
        Path.GetFullPath(Path.Combine([Path.DirectorySeparatorChar.ToString(), .. segments]));

    private sealed class ScriptedGit : IGitWorktreeIdentity
    {
        private readonly Dictionary<string, (string Top, string Common)> _known = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public ScriptedGit Add(string directory, string top, string common)
        {
            _known[directory] = (top, common);
            return this;
        }

        public int CallsFor(string directory)
        {
            lock (_calls) return _calls.GetValueOrDefault(directory);
        }

        public Task<GitWorktreeRoots> TryRootsAsync(string directory, CancellationToken cancellationToken = default)
        {
            lock (_calls) _calls[directory] = _calls.GetValueOrDefault(directory) + 1;
            return Task.FromResult(
                _known.TryGetValue(directory, out (string Top, string Common) found)
                    ? new GitWorktreeRoots(found.Top, found.Common, null)
                    : new GitWorktreeRoots(null, null, "not a git repository"));
        }
    }
}
