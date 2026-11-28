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
    public void RelativeProjectPathIsResolvedAgainstTheProcessWorkingDirectory()
    {
        string worktree = Path.Combine(Work, "wt");
        _files.CreateDirectory(worktree);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(Server, Server, Common).Add(worktree, worktree, Common));

        bool resolved = registry.TryResolve("wt", out DocsTools? _, out string? refusal);

        Assert.True(resolved, refusal);
        Assert.Equal([worktree], _built);
    }

    [Fact]
    public void ProjectPathThatIsNotADirectoryIsRefusedWithItsFullPath()
    {
        DocsRootRegistry registry = Registry(new ScriptedGit());

        bool resolved = registry.TryResolve("missing", out DocsTools? tools, out string? refusal);

        Assert.False(resolved);
        Assert.Null(tools);
        Assert.Contains($"projectPath '{Path.Combine(Work, "missing")}' is not a directory", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectPathOfAnotherRepositoryIsRefused()
    {
        string other = Absolute("kw", "other");
        _files.CreateDirectory(other);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(Server, Server, Common).Add(other, other, Absolute("kw", "other", ".git")));

        bool resolved = registry.TryResolve(other, out _, out string? refusal);

        Assert.False(resolved);
        Assert.Contains("belongs to a different repository", refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public void ServerRootThatIsNotARepositoryRefusesEveryProjectPath()
    {
        string worktree = Path.Combine(Work, "wt");
        _files.CreateDirectory(worktree);
        DocsRootRegistry registry = Registry(new ScriptedGit().Add(worktree, worktree, Common));

        bool resolved = registry.TryResolve(worktree, out _, out string? refusal);

        Assert.False(resolved);
        Assert.Contains("is not a git repository", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperatingSystemKind.Linux, 1)]
    [InlineData(OperatingSystemKind.MacOS, 0)]
    [InlineData(OperatingSystemKind.Windows, 0)]
    public void TopLevelsThatDifferOnlyInCaseAreTheSameRootOnlyWhereTheHostFoldsCase(OperatingSystemKind platform, int toolsBuilt)
    {
        string shouting = Absolute("kw", "SERVER");
        _files.CreateDirectory(shouting);
        DocsRootRegistry registry = Registry(
            new ScriptedGit().Add(Server, Server, Common).Add(shouting, shouting, Common),
            platform);

        bool resolved = registry.TryResolve(shouting, out _, out string? refusal);

        Assert.True(resolved, refusal);
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
    public void BoundModeAsksGitOnceForARepeatedProjectPath()
    {
        string worktree = Path.Combine(Work, "wt");
        _files.CreateDirectory(worktree);
        ScriptedGit git = new ScriptedGit().Add(Server, Server, Common).Add(worktree, worktree, Common);
        DocsRootRegistry registry = Registry(git);

        for (int call = 0; call < 3; call++)
        {
            Assert.True(registry.TryResolve(worktree, out _, out string? refusal), refusal);
        }

        Assert.Equal(1, git.CallsFor(worktree));
        Assert.Equal([worktree], _built);
    }

    [Fact]
    public void AnyRepoPathInsideAnInitializedRepositoryResolvesToItsTopLevel()
    {
        string inside = Path.Combine(RepoA, "src");
        _files.CreateDirectory(inside);
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit().Add(inside, RepoA, Path.Combine(RepoA, ".git")));

        bool resolved = registry.TryResolve(inside, out DocsTools? tools, out string? refusal);

        Assert.True(resolved, refusal);
        Assert.NotNull(tools);
        Assert.Equal([RepoA], _built);
    }

    [Fact]
    public void AnyRepoPathThatIsNotADirectoryIsRefused()
    {
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit());

        bool resolved = registry.TryResolve(Path.Combine(Work, "missing"), out DocsTools? tools, out string? refusal);

        Assert.False(resolved);
        Assert.Null(tools);
        Assert.Contains("is not a directory", refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public void AnyRepoDirectoryOutsideGitIsRefused()
    {
        string plain = Absolute("kw", "plain");
        _files.CreateDirectory(plain);
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit());

        bool resolved = registry.TryResolve(plain, out DocsTools? tools, out string? refusal);

        Assert.False(resolved);
        Assert.Null(tools);
        Assert.Contains("is not inside a git repository", refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public void AnyRepoGitRepositoryWithoutHostConfigurationIsRefused()
    {
        string bare = Absolute("kw", "bare");
        _files.CreateDirectory(bare);
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit().Add(bare, bare, Path.Combine(bare, ".git")));

        bool resolved = registry.TryResolve(bare, out DocsTools? tools, out string? refusal);

        Assert.False(resolved);
        Assert.Null(tools);
        Assert.Contains("has no Kyber-Weave host configuration", refusal, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs init", refusal, StringComparison.Ordinal);
        Assert.Empty(_built);
    }

    [Fact]
    public void AnyRepoBlankProjectPathIsRefused()
    {
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit());

        bool resolved = registry.TryResolve("  ", out DocsTools? tools, out string? refusal);

        Assert.False(resolved);
        Assert.Null(tools);
        Assert.Contains("projectPath is required", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyRepoAsksGitOnceForARepeatedProjectPath()
    {
        ScriptedGit git = new ScriptedGit().Add(RepoA, RepoA, Path.Combine(RepoA, ".git"));
        DocsRootRegistry registry = AnyRegistry(git);

        for (int call = 0; call < 3; call++)
        {
            Assert.True(registry.TryResolve(RepoA, out _, out string? refusal), refusal);
        }

        Assert.Equal(1, git.CallsFor(RepoA));
        Assert.Equal([RepoA], _built);
    }

    [Fact]
    public void AnyRepoTwoPathsInOneRepositoryShareOneSetOfTools()
    {
        string inside = Path.Combine(RepoA, "src");
        _files.CreateDirectory(inside);
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit()
            .Add(RepoA, RepoA, Path.Combine(RepoA, ".git"))
            .Add(inside, RepoA, Path.Combine(RepoA, ".git")));

        Assert.True(registry.TryResolve(RepoA, out DocsTools? first, out string? refusal), refusal);
        Assert.True(registry.TryResolve(inside, out DocsTools? second, out refusal), refusal);

        Assert.Same(first, second);
        Assert.Equal([RepoA], _built);
        Assert.Equal(1, registry.Count);
    }

    /// <summary>
    /// The root a fifth one evicts is the one used least recently, not the one built first: the first root is used
    /// again before the fifth arrives, so a first-in-first-out cache would evict it and this one must not.
    /// </summary>
    [Fact]
    public void AnyRepoFifthRootEvictsTheLeastRecentlyUsed()
    {
        string[] roots = InitializedRoots(5);
        DocsRootRegistry registry = AnyRegistry(Git(roots));

        foreach (string root in roots[..4])
        {
            Assert.True(registry.TryResolve(root, out _, out string? refusal), refusal);
        }

        Assert.True(registry.TryResolve(roots[0], out _, out string? again), again);
        Assert.True(registry.TryResolve(roots[4], out _, out again), again);
        Assert.Equal(DocsRootRegistry.Capacity, registry.Count);
        Assert.Equal(5, _built.Count);

        Assert.True(registry.TryResolve(roots[0], out _, out again), again);
        Assert.Equal(1, _built.Count(root => root == roots[0]));

        Assert.True(registry.TryResolve(roots[1], out _, out again), again);
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
        Assert.True(registry.TryResolve(roots[1], out DocsTools? cached, out string? refusal), refusal);

        Task<(bool Resolved, DocsTools? Tools)> slow = Task.Run(() => Resolve(registry, roots[0]), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        bool resolved = registry.TryResolve(roots[1], out DocsTools? again, out refusal);

        Assert.True(resolved, refusal);
        Assert.Same(cached, again);
        Assert.False(slow.IsCompleted);
        release.Set();
        (bool slowResolved, DocsTools? slowTools) = await slow;
        Assert.True(slowResolved);
        Assert.NotNull(slowTools);
    }

    [Fact]
    public async Task AnyRepoTwoConcurrentFirstCallsForOneRootEndWithOneSharedInstance()
    {
        using Barrier both = new(2);
        _onBuild = _ => Assert.True(both.SignalAndWait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit().Add(RepoA, RepoA, Path.Combine(RepoA, ".git")));

        Task<(bool Resolved, DocsTools? Tools)> first = Task.Run(() => Resolve(registry, RepoA), TestContext.Current.CancellationToken);
        Task<(bool Resolved, DocsTools? Tools)> second = Task.Run(() => Resolve(registry, RepoA), TestContext.Current.CancellationToken);
        (bool Resolved, DocsTools? Tools)[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Resolved));
        Assert.Same(results[0].Tools, results[1].Tools);
        Assert.Equal(2, _built.Count);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void AnyRepoCallHoldingAnEvictedRootFinishesOnItsToolsAndTheNextCallBuildsNew()
    {
        string[] roots = InitializedRoots(5);
        DocsRootRegistry registry = AnyRegistry(Git(roots));
        Assert.True(registry.TryResolve(roots[0], out DocsTools? held, out string? refusal), refusal);

        foreach (string other in roots[1..])
        {
            Assert.True(registry.TryResolve(other, out _, out refusal), refusal);
        }

        Assert.Equal(DocsRootRegistry.Capacity, registry.Count);
        Assert.NotNull(held);
        Assert.Contains("mode=any-repo", held.DocsStatus(), StringComparison.Ordinal);

        Assert.True(registry.TryResolve(roots[0], out DocsTools? rebuilt, out refusal), refusal);
        Assert.NotSame(held, rebuilt);
        Assert.Equal(2, _built.Count(root => root == roots[0]));
    }

    [Fact]
    public void AnyRepoFactoryFailureIsARefusalForThatCallOnlyAndLeavesNoEntry()
    {
        int attempts = 0;
        _onBuild = _ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("the corpus could not be built");
        };
        DocsRootRegistry registry = AnyRegistry(new ScriptedGit().Add(RepoA, RepoA, Path.Combine(RepoA, ".git")));

        bool failed = registry.TryResolve(RepoA, out DocsTools? none, out string? refusal);

        Assert.False(failed);
        Assert.Null(none);
        Assert.Contains("the corpus could not be built", refusal, StringComparison.Ordinal);
        Assert.Equal(0, registry.Count);
        Assert.True(registry.TryResolve(RepoA, out DocsTools? tools, out refusal), refusal);
        Assert.NotNull(tools);
    }

    private static (bool Resolved, DocsTools? Tools) Resolve(DocsRootRegistry registry, string projectPath)
    {
        bool resolved = registry.TryResolve(projectPath, out DocsTools? tools, out string? _);
        return (resolved, tools);
    }

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

        public bool TryRoots(string directory, out string? topLevel, out string? commonDirectory, out string? failure)
        {
            lock (_calls) _calls[directory] = _calls.GetValueOrDefault(directory) + 1;
            if (_known.TryGetValue(directory, out (string Top, string Common) found))
            {
                (topLevel, commonDirectory, failure) = (found.Top, found.Common, null);
                return true;
            }

            (topLevel, commonDirectory, failure) = (null, null, "not a git repository");
            return false;
        }
    }
}
