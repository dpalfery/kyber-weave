using System.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The per-call <c>projectPath</c>: a docs tool answers for the linked worktree it names, refuses a
/// path outside the server's repository, and keeps only a few worktrees at once.
/// </summary>
/// <remarks>
/// Each case builds a real repository and real linked worktrees under a temporary root, because the
/// rule being tested is git's own: two paths belong together when they share a common git directory.
/// </remarks>
public sealed class McpProjectPathTests : IDisposable
{
    private const string OntologyPage = "docs/documentation-ontology.md";

    /// <summary>A question about a subject no scaffolded page covers, answered by the sentence written into one checkout only.</summary>
    private const string Marker = "How does the quokka reconciler flush the zymurgy ledger?";

    private const string MarkerSentence = "The quokka reconciler flushes the zymurgy ledger nightly, and only this page says so.";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ProjectPathAnswersFromTheWorktreeWhileTheServerRootKeepsItsOwnDocuments()
    {
        string repo = CreateRepository();
        string worktree = AddWorktree(repo, "wt");
        File.AppendAllText(Path.Combine(worktree, OntologyPage), $"\n\n{MarkerSentence}\n");
        DocsTools tools = ServerTools(repo);

        string fromRoot = tools.Explore(Marker);
        string fromWorktree = tools.Explore(Marker, projectPath: worktree);

        Assert.Contains("real miss", fromRoot, StringComparison.Ordinal);
        Assert.Contains("only this page says so", fromWorktree, StringComparison.Ordinal);
        Assert.Matches(@"^provenance: root=\S*/wt branch=wt ", fromWorktree);
        Assert.Matches(@"^provenance: root=\S*/repo branch=", fromRoot);
    }

    [Fact]
    public void ProjectPathNamingASubdirectoryOrTheServerRootIsAccepted()
    {
        string repo = CreateRepository();
        string worktree = AddWorktree(repo, "wt");
        File.AppendAllText(Path.Combine(worktree, OntologyPage), $"\n\n{MarkerSentence}\n");
        DocsTools tools = ServerTools(repo);

        string fromSubdirectory = tools.Explore(Marker, projectPath: Path.Combine(worktree, "docs"));
        string fromRootPath = tools.DocsStatus(repo);

        Assert.Contains("only this page says so", fromSubdirectory, StringComparison.Ordinal);
        Assert.Matches(@"^provenance: root=\S*/repo branch=", fromRootPath);
    }

    [Fact]
    public void ProjectPathInAnotherRepositoryIsRefused()
    {
        string repo = CreateRepository();
        string foreign = Path.Combine(_temp.Path, "foreign");
        Directory.CreateDirectory(foreign);
        Git(foreign, "init", "-q");
        DocsTools tools = ServerTools(repo);

        string response = tools.Explore("ontology", projectPath: foreign);

        Assert.Contains("projectPath was refused", response, StringComparison.Ordinal);
        Assert.Contains("different repository", response, StringComparison.Ordinal);
        Assert.DoesNotContain("confidence:", response, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectPathThatIsNotAGitDirectoryOrDoesNotExistIsRefused()
    {
        string repo = CreateRepository();
        string plain = Path.Combine(_temp.Path, "plain");
        Directory.CreateDirectory(plain);
        DocsTools tools = ServerTools(repo);

        string notGit = tools.Explore("ontology", projectPath: plain);
        string missing = tools.Explore("ontology", projectPath: Path.Combine(_temp.Path, "absent"));

        Assert.Contains("not inside a git repository", notGit, StringComparison.Ordinal);
        Assert.Contains("not a directory", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryKeepsAtMostFourWorktreesAndReusesTheirTools()
    {
        string repo = CreateRepository();
        DocsRootRegistry registry = Registry(repo);
        DocsTools tools = ServerTools(repo, registry);
        string[] worktrees = [.. Enumerable.Range(1, 5).Select(i => AddWorktree(repo, "wt" + i))];

        foreach (string worktree in worktrees) tools.DocsStatus(worktree);
        Assert.Equal(DocsRootRegistry.Capacity, registry.Count);

        Assert.True(registry.TryResolve(worktrees[4], out DocsTools? first, out _));
        Assert.True(registry.TryResolve(worktrees[4], out DocsTools? again, out _));
        Assert.Same(first, again);
    }

    private static DocsRootRegistry Registry(string repo) =>
        new(repo, root => DocsRootRegistry.CreateTools(root, codeGraphRefreshEnabled: false, registry: null));

    private static DocsTools ServerTools(string repo, DocsRootRegistry? registry = null) =>
        DocsRootRegistry.CreateTools(repo, codeGraphRefreshEnabled: false, registry ?? Registry(repo));

    private string CreateRepository()
    {
        string repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        DocsScaffolder.Scaffold(repo);
        Git(repo, "add", "-A");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
        return repo;
    }

    private string AddWorktree(string repo, string name)
    {
        string path = Path.Combine(_temp.Path, name);
        Git(repo, "worktree", "add", "-q", "-b", name, path);
        return path;
    }

    private static void Git(string directory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".no-global-gitconfig");
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}
