using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the two provenance fields a concurrent-worktree run depends on: <c>branch=</c> and
/// <c>repo=</c>.
/// </summary>
/// <remarks>
/// <para>
/// Before these fields existed the provenance header named only <c>root=</c>. Two worktrees of one
/// repository therefore looked like two different repositories, and an agent that compared
/// <c>root=</c> to its own checkout was told it had found the wrong repository when it had in fact
/// found the same repository on another branch. Naming the branch and the shared git directory is
/// what lets a reader tell "another worktree of this repository" from "a different repository",
/// which is the distinction the per-role worktree rule is written against.
/// </para>
/// <para>
/// The assertions read the line through the public <see cref="DocsTools.DocsStatus"/> tool, because
/// that tool is the documented way a caller asks which corpus answered. They assert that the two
/// fields exist and carry real values; the position of the fields in the line is not asserted, so
/// the implementation is free to place them where it reads best.
/// </para>
/// </remarks>
public sealed class CorpusProvenanceTests : IDisposable
{
    /// <summary>The branch field, capturing its value. Anchored on a preceding space so <c>repo=</c> cannot match it.</summary>
    private static readonly Regex BranchField = new(@"(?:^|\s)branch=(?<value>\S+)", RegexOptions.CultureInvariant);

    /// <summary>The repository field, capturing its value without truncating paths that contain spaces.</summary>
    private static readonly Regex RepoField = new(
        @"(?:^|\s)repo=(?<value>.*?)(?=\s+(?:rev|dirty|documents|branch)=|$)",
        RegexOptions.CultureInvariant);

    /// <summary>Values a field carries when the information could not be read, never when it was.</summary>
    private static readonly string[] PlaceholderValues =
        ["", "unknown", "unavailable", "none", "null", "-"];

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// Every provenance line names the branch and the git directory, so a caller can decide whether
    /// two answers came from one repository without guessing from <c>root=</c> alone.
    /// </summary>
    /// <remarks>
    /// D24: <c>branch=</c> and <c>repo=</c> are the fields the worktree rule is written against. A
    /// response that omits them leaves the reader with only <c>root=</c>, which is exactly the
    /// signal that cannot separate a second worktree from a foreign repository.
    /// </remarks>
    [Fact]
    public void ProvenanceLineNamesTheBranchAndTheRepositoryGitDirectory()
    {
        DocsScaffolder.Scaffold(_root.Path);

        string line = Status(_root.Path);

        Assert.Matches(BranchField, line);
        Assert.Matches(RepoField, line);
    }

    /// <summary>
    /// In a real checkout the two fields carry the values git reports, not placeholders.
    /// </summary>
    /// <remarks>
    /// A field that is present but always <c>unknown</c> would satisfy a presence check and answer
    /// nothing. The branch is read here from <c>.git/HEAD</c> rather than by running git, so the
    /// expectation is independent of the implementation and the test starts no child process.
    /// </remarks>
    [Fact]
    public void ProvenanceBranchAndRepoCarryTheCheckoutValues()
    {
        TestGitRepositoryTemplate.CreateReviewGatesCouncilRepository(_root.Path);
        DocsScaffolder.Scaffold(_root.Path);

        string branch = BranchFromHead(_root.Path);
        string line = Status(_root.Path);

        string servedBranch = FieldValue(BranchField, line);
        string servedRepo = FieldValue(RepoField, line);

        Assert.Equal(branch, servedBranch);
        Assert.True(
            !PlaceholderValues.Contains(servedRepo, StringComparer.OrdinalIgnoreCase),
            $"The provenance line names the repository directory '{servedRepo}', which is a placeholder "
            + "rather than the git directory of this checkout.");
        Assert.Contains(".git", servedRepo, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repository path with spaces is captured completely in the provenance line without truncation.
    /// </summary>
    [Fact]
    public void ProvenanceRepoWithSpacesInPathIsCapturedWithoutTruncation()
    {
        string spacedRoot = Path.Combine(_root.Path, "repo with spaces");
        Directory.CreateDirectory(spacedRoot);
        TestGitRepositoryTemplate.CreateReviewGatesCouncilRepository(spacedRoot);
        DocsScaffolder.Scaffold(spacedRoot);

        string line = Status(spacedRoot);
        string servedRepo = FieldValue(RepoField, line);

        Assert.Contains("repo with spaces", servedRepo, StringComparison.Ordinal);
        Assert.Contains(".git", servedRepo, StringComparison.Ordinal);
    }

    /// <summary>
    /// An untrusted .git pointer containing illegal path characters does not throw an uncaught
    /// exception, but degrades gracefully to unavailable.
    /// </summary>
    [Fact]
    public void CorruptDotGitWithIllegalCharacters_DoesNotThrow_DegradesGracefully()
    {
        string corruptRoot = Path.Combine(_root.Path, "corrupt-dot-git");
        Directory.CreateDirectory(corruptRoot);
        DocsScaffolder.Scaffold(corruptRoot);
        File.WriteAllText(Path.Combine(corruptRoot, ".git"), "gitdir: \0illegal\0path");

        string line = Status(corruptRoot);
        string servedBranch = FieldValue(BranchField, line);
        string servedRepo = FieldValue(RepoField, line);

        Assert.Equal("unavailable", servedBranch);
        Assert.Equal("unavailable", servedRepo);
    }

    /// <summary>
    /// A commondir pointer containing illegal path characters does not throw, but falls back to
    /// the checkout's git directory.
    /// </summary>
    [Fact]
    public void CorruptCommonDirWithIllegalCharacters_DoesNotThrow_FallsBackToGitDirectory()
    {
        string corruptRoot = Path.Combine(_root.Path, "corrupt-commondir");
        Directory.CreateDirectory(corruptRoot);
        DocsScaffolder.Scaffold(corruptRoot);
        string dotGit = Path.Combine(corruptRoot, ".git");
        Directory.CreateDirectory(dotGit);
        File.WriteAllText(Path.Combine(dotGit, "commondir"), "\0illegal\0path");
        File.WriteAllText(Path.Combine(dotGit, "HEAD"), "ref: refs/heads/main\n");

        string line = Status(corruptRoot);
        string servedBranch = FieldValue(BranchField, line);
        string servedRepo = FieldValue(RepoField, line);

        Assert.Equal("main", servedBranch);
        Assert.Contains(".git", servedRepo, StringComparison.Ordinal);
    }

    /// <summary>
    /// The branch named by <c>.git/HEAD</c>, which is what a checkout reports without running git.
    /// </summary>
    private static string BranchFromHead(string root)
    {
        string head = File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim();
        const string prefix = "ref: refs/heads/";
        Assert.StartsWith(prefix, head, StringComparison.Ordinal);
        return head[prefix.Length..];
    }

    private static string FieldValue(Regex field, string line)
    {
        Match match = field.Match(line);
        Assert.True(
            match.Success,
            $"The provenance line '{line.Trim()}' does not carry the '{field}' field.");
        return match.Groups["value"].Value;
    }

    /// <summary>The provenance block as the docs status tool returns it, which is the caller-visible contract.</summary>
    private static string Status(string root)
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(root);
        DocumentIndexHost host = new(
            root,
            () => FakeCodeGraphResolver.WithSymbols(),
            () => new DocumentLoader(root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        string status = new DocsTools(host).DocsStatus();
        Assert.StartsWith("provenance:", status, StringComparison.Ordinal);
        return status;
    }
}
