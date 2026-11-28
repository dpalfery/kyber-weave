using System.Security.Cryptography;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The repository locator over the git, file-system, path and environment seams: the root comes from git when it
/// answers, from the nearest <c>.git</c> entry otherwise, and every path is resolved against the injected current
/// directory rather than the process's own. The arbiter directory is the user-level store under the home directory
/// once it holds the repository's ledger or decision log, and the project's <c>artifacts/arbiter</c> until then.
/// </summary>
public sealed class ArbiterRepositoryLocatorTests
{
    private const string WorkingDirectory = "/kw/work";

    private readonly InMemoryFileSystem _files = new();

    public ArbiterRepositoryLocatorTests() =>
        _environment = new FakeProcessEnvironment(
            currentDirectory: WorkingDirectory,
            isFileSystemCaseInsensitive: !_files.CaseSensitive);
    private readonly FakeProcessEnvironment _environment;

    private ArbiterRepositoryLocator Locator(GitOutcome? gitOutcome = null) =>
        new(
            new FakeGit((_, _) => gitOutcome ?? new GitOutcome(1, string.Empty, "not a git repository")),
            _files,
            new FakePathResolver(_files),
            _environment);

    [Fact]
    public void TheRootIsWhatGitReports()
    {
        _files.CreateDirectory("/kw/checkout");

        Assert.Equal("/kw/checkout", Locator(new GitOutcome(0, "/kw/checkout\n", string.Empty)).ResolveRepositoryRoot(WorkingDirectory));
    }

    [Fact]
    public void AGitAnswerIsTakenAsAFullPath()
    {
        _files.CreateDirectory("/kw/checkout");

        Assert.Equal("/kw/checkout", Locator(new GitOutcome(0, "/kw/checkout/", string.Empty)).ResolveRepositoryRoot(WorkingDirectory));
    }

    [Fact]
    public void ARelativeGitAnswerResolvesAgainstTheDirectoryGitRanIn()
    {
        _files.CreateDirectory("/kw/work/project/checkout");

        Assert.Equal(
            "/kw/work/project/checkout",
            Locator(new GitOutcome(0, "checkout", string.Empty)).ResolveRepositoryRoot("/kw/work/project"));
    }

    [Fact]
    public void AGitAnswerThatNamesNoPathLeavesTheInjectedPathAsTheRoot()
    {
        _files.CreateDirectory(WorkingDirectory);

        Assert.Equal(WorkingDirectory, Locator(new GitOutcome(0, "a\0b", string.Empty)).ResolveRepositoryRoot(WorkingDirectory));
    }

    [Fact]
    public void AGitThatCannotAnswerLeavesTheInjectedPathAsTheRoot()
    {
        _files.CreateDirectory(WorkingDirectory);

        Assert.Equal(WorkingDirectory + "/nested", Locator().ResolveRepositoryRoot("nested"));
    }

    [Fact]
    public void TheArbiterDirectorySitsUnderTheArtifactsFolder()
    {
        _files.CreateDirectory("/kw/checkout");

        Assert.Equal(
            "/kw/checkout/artifacts/arbiter",
            Locator(new GitOutcome(0, "/kw/checkout", string.Empty)).ResolveArbiterDirectory(WorkingDirectory));
    }

    [Fact]
    public void TheArbiterDirectoryIsTheUserLevelStoreWhenItHoldsTheRepositoriesLedger()
    {
        string main = MainCheckout;
        _files.CreateDirectory(main);
        string store = UserLevelStoreOf(main);
        _files.CreateDirectory(store);
        _files.WriteAllText(Path.Combine(store, InFlightLedger.FileName), "{}\n");

        Assert.Equal(store, ScriptedLocator(GitNaming(main, Path.Combine(main, ".git"))).ResolveArbiterDirectory(main));
    }

    [Fact]
    public void ALinkedWorktreeReadsItsMainCheckoutsUserLevelStore()
    {
        // A run works in a linked worktree, often one parked inside the main checkout, while the user-level hook keyed
        // the store by the main checkout: a command run inside the worktree must derive that same key.
        string main = MainCheckout;
        string worktree = Path.Combine(main, ".agent_worktrees", "run");
        _files.CreateDirectory(worktree);
        string store = UserLevelStoreOf(main);
        _files.CreateDirectory(store);
        _files.WriteAllText(Path.Combine(store, InFlightLedger.FileName), "{}\n");
        ArbiterRepositoryLocator locator = ScriptedLocator(GitNaming(worktree, Path.Combine(main, ".git")));

        Assert.Equal(store, locator.ResolveArbiterDirectory(worktree));
        Assert.Equal(store, locator.ResolveArbiterDirectory(Path.Combine(worktree, "src", "deep")));
    }

    [Fact]
    public void TheArbiterDirectoryIsTheUserLevelStoreWhenItHoldsOnlyADecisionLog()
    {
        string main = MainCheckout;
        _files.CreateDirectory(main);
        string store = UserLevelStoreOf(main);
        _files.CreateDirectory(store);
        _files.WriteAllText(Path.Combine(store, DecisionLog.FileName), "{}\n");

        Assert.Equal(store, ScriptedLocator(GitNaming(main, Path.Combine(main, ".git"))).ResolveArbiterDirectory(main));
    }

    [Fact]
    public void TheArbiterDirectoryStaysUnderTheArtifactsFolderWhileTheUserLevelStoreHoldsNoLog()
    {
        // A project-scope install: the directory under the home may exist for the hook's other files, and the project
        // has its own ledger, so the report keeps reading the project's store.
        string main = MainCheckout;
        _files.CreateDirectory(Path.Combine(main, "artifacts", "arbiter"));
        _files.WriteAllText(Path.Combine(main, "artifacts", "arbiter", InFlightLedger.FileName), "{}\n");
        string store = UserLevelStoreOf(main);
        _files.CreateDirectory(store);
        _files.WriteAllText(Path.Combine(store, "snapshots.json"), "[]");

        Assert.Equal(
            Path.Combine(main, "artifacts", "arbiter"),
            ScriptedLocator(GitNaming(main, Path.Combine(main, ".git"))).ResolveArbiterDirectory(main));
    }

    [Fact]
    public void WithoutAHomeDirectoryTheArbiterDirectoryStaysUnderTheArtifactsFolder()
    {
        string main = MainCheckout;
        _files.CreateDirectory(main);

        Assert.Equal(
            Path.Combine(main, "artifacts", "arbiter"),
            ScriptedLocator(GitNaming(main, Path.Combine(main, ".git")), homeDirectory: string.Empty).ResolveArbiterDirectory(main));
    }

    [Fact]
    public void AGitThatCannotNameTheCommonDirectoryKeysTheUserLevelStoreByTheRepositoryRoot()
    {
        string main = MainCheckout;
        _files.CreateDirectory(main);
        string store = UserLevelStoreOf(main);
        _files.CreateDirectory(store);
        _files.WriteAllText(Path.Combine(store, InFlightLedger.FileName), "{}\n");

        Assert.Equal(store, ScriptedLocator(GitNaming(main, commonDirectory: null)).ResolveArbiterDirectory(main));
    }

    [Fact]
    public void FindGitRootWalksUpToTheNearestGitEntry()
    {
        _files.CreateDirectory("/kw/work/project/.git");
        _files.CreateDirectory("/kw/work/project/src/deep");

        Assert.Equal("/kw/work/project", Locator().FindGitRoot("/kw/work/project/src/deep"));
    }

    [Fact]
    public void FindGitRootAnswersTheResolvedStartWhenNoGitEntryExists()
    {
        _files.CreateDirectory("/kw/work/plain");

        Assert.Equal("/kw/work/plain", Locator().FindGitRoot("plain"));
    }

    /// <summary>The operator's home, platform-qualified, where the user-level hook keeps one store per repository.</summary>
    private string Home => Path.Combine(_files.Root, "kw", "home");

    /// <summary>The main checkout of the repository the user-level tests record under.</summary>
    private string MainCheckout => Path.Combine(_files.Root, "kw", "main");

    /// <summary>
    /// The directory the user-level hook records under for <paramref name="root" />: the layout and the key are spelled
    /// out here rather than taken from the code under test, so this test and the hook's own pin one directory.
    /// </summary>
    private string UserLevelStoreOf(string root) =>
        Path.Combine(
            Home, ".kyber-weave", "arbiter", "projects",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root))));

    /// <summary>The locator over a git the test scripts and the home the test names.</summary>
    /// <param name="run">Answers one git invocation.</param>
    /// <param name="homeDirectory">The operator's home; <see cref="Home" /> when null.</param>
    private ArbiterRepositoryLocator ScriptedLocator(Func<string, IReadOnlyList<string>, GitOutcome> run, string? homeDirectory = null) =>
        new(
            new FakeGitCommandRunner(run),
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(
                currentDirectory: WorkingDirectory,
                homeDirectory: homeDirectory ?? Home,
                isFileSystemCaseInsensitive: !_files.CaseSensitive));

    /// <summary>
    /// A git that names <paramref name="topLevel" /> as the working tree's root and <paramref name="commonDirectory" />
    /// as the repository's common git directory; a null common directory is one git cannot name.
    /// </summary>
    private static Func<string, IReadOnlyList<string>, GitOutcome> GitNaming(string topLevel, string? commonDirectory) =>
        (_, arguments) => arguments.Contains("--git-common-dir", StringComparer.Ordinal)
            ? commonDirectory is null
                ? new GitOutcome(128, string.Empty, "fatal: not a git repository")
                : new GitOutcome(0, commonDirectory + Environment.NewLine, string.Empty)
            : new GitOutcome(0, topLevel + Environment.NewLine, string.Empty);

    private sealed class FakeGit(Func<string, IReadOnlyList<string>, GitOutcome> run) : IGitCommandRunner
    {
        public GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments) => run(workingDirectory, arguments);

        public Task<GitOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(run(workingDirectory, arguments));
    }
}
