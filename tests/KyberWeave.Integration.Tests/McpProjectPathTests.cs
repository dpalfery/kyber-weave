using System.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using Xunit;

using KyberWeave.Tests.Fakes;
namespace KyberWeave.Integration.Tests;

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
    public async Task ProjectPathAnswersFromTheWorktreeWhileTheServerRootKeepsItsOwnDocuments()
    {
        string repo = await CreateRepositoryAsync();
        string worktree = await AddWorktree(repo, "wt");
        await File.AppendAllTextAsync(Path.Combine(worktree, OntologyPage), $"\n\n{MarkerSentence}\n");
        DocsTools tools = ServerTools(repo);

        string fromRoot = await tools.ExploreAsync(Marker);
        string fromWorktree = await tools.ExploreAsync(Marker, projectPath: worktree);

        Assert.Contains("real miss", fromRoot, StringComparison.Ordinal);
        Assert.Contains("only this page says so", fromWorktree, StringComparison.Ordinal);
        Assert.Matches(@"^provenance: root=\S*/wt branch=wt ", fromWorktree);
        Assert.Matches(@"^provenance: root=\S*/repo branch=", fromRoot);
    }

    [Fact]
    public async Task ProjectPathNamingASubdirectoryOrTheServerRootIsAccepted()
    {
        string repo = await CreateRepositoryAsync();
        string worktree = await AddWorktree(repo, "wt");
        await File.AppendAllTextAsync(Path.Combine(worktree, OntologyPage), $"\n\n{MarkerSentence}\n");
        DocsTools tools = ServerTools(repo);

        string fromSubdirectory = await tools.ExploreAsync(Marker, projectPath: Path.Combine(worktree, "docs"));
        string fromRootPath = await tools.DocsStatusAsync(repo);

        Assert.Contains("only this page says so", fromSubdirectory, StringComparison.Ordinal);
        Assert.Matches(@"^provenance: root=\S*/repo branch=", fromRootPath);
    }

    [Fact]
    public async Task ProjectPathInAnotherRepositoryIsRefused()
    {
        string repo = await CreateRepositoryAsync();
        string foreign = Path.Combine(_temp.Path, "foreign");
        Directory.CreateDirectory(foreign);
        await Git(foreign, "init", "-q");
        DocsTools tools = ServerTools(repo);

        string response = await tools.ExploreAsync("ontology", projectPath: foreign);

        Assert.Contains("projectPath was refused", response, StringComparison.Ordinal);
        Assert.Contains("different repository", response, StringComparison.Ordinal);
        Assert.DoesNotContain("confidence:", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectPathThatIsNotAGitDirectoryOrDoesNotExistIsRefused()
    {
        string repo = await CreateRepositoryAsync();
        string plain = Path.Combine(_temp.Path, "plain");
        Directory.CreateDirectory(plain);
        DocsTools tools = ServerTools(repo);

        string notGit = await tools.ExploreAsync("ontology", projectPath: plain);
        string missing = await tools.ExploreAsync("ontology", projectPath: Path.Combine(_temp.Path, "absent"));

        Assert.Contains("not inside a git repository", notGit, StringComparison.Ordinal);
        Assert.Contains("not a directory", missing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryKeepsAtMostFourWorktreesAndReusesTheirTools()
    {
        string repo = await CreateRepositoryAsync();
        DocsRootRegistry registry = Registry(repo);
        DocsTools tools = ServerTools(repo, registry);
        List<string> worktreeList = [];
        for (int i = 1; i <= 5; i++)
            worktreeList.Add(await AddWorktree(repo, "wt" + i));
        string[] worktrees = [.. worktreeList];

        foreach (string worktree in worktrees) await tools.DocsStatusAsync(worktree);
        Assert.Equal(DocsRootRegistry.Capacity, registry.Count);

        DocsRootRegistry.DocsRootResolution first = await registry.TryResolveAsync(worktrees[4]);
        Assert.True(first.Resolved, first.Refusal);
        DocsRootRegistry.DocsRootResolution again = await registry.TryResolveAsync(worktrees[4]);
        Assert.True(again.Resolved, again.Refusal);
        Assert.Same(first.Tools, again.Tools);
    }

    private static DocsRootRegistry Registry(string repo) =>
        DocsTestCommands.Registry(repo, root => DocsRootRegistry.CreateTools(root, codeGraphRefreshEnabled: false, registry: null));

    private static DocsTools ServerTools(string repo, DocsRootRegistry? registry = null) =>
        DocsRootRegistry.CreateTools(repo, codeGraphRefreshEnabled: false, registry ?? Registry(repo));

    private async Task<string> CreateRepositoryAsync()
    {
        string repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(repo);
        await Git(repo, "init", "-q", "-b", "main");
        await DocsTestCommands.Scaffolding.ScaffoldAsync(repo);
        await Git(repo, "add", "-A");
        await Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
        return repo;
    }

    private async Task<string> AddWorktree(string repo, string name)
    {
        string path = Path.Combine(_temp.Path, name);
        await Git(repo, "worktree", "add", "-q", "-b", name, path);
        return path;
    }

    private static async Task Git(string directory, params string[] arguments)
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

        ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}
