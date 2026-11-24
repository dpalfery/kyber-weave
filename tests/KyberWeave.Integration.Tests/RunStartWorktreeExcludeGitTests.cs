using KyberWeave.Application.RunStart;
using KyberWeave.Core.RunStart;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Data.RunStart;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
///     The worktree folder stays out of git's status in a real repository: git itself reads the exclude that the
///     run start step writes, from the main checkout and from a linked worktree.
/// </summary>
/// <remarks>
///     The in-memory tests script what <c>git rev-parse --git-path info/exclude</c> answers. This class asks real
///     git, so a difference between the answer git gives and the answer the code expects shows up here.
/// </remarks>
public sealed class RunStartWorktreeExcludeGitTests : IDisposable
{
    private const string Folder = ".agent_worktrees";
    private const string LinkedBranch = "claude/fix/linked-exclude";
    private readonly RunStartProbe _probe = RunStartProbe.Create();
    private readonly ProcessGitCommandRunner _git = new(ProcessGitCommandRunner.DefaultTimeout);

    /// <inheritdoc />
    public void Dispose()
    {
        _probe.Dispose();
    }

    /// <summary>
    ///     A dirty checkout gets its worktree inside the checkout through the real run start flow, and git status
    ///     does not list that folder.
    /// </summary>
    [Fact]
    public void TheRealRunStartFlowKeepsTheWorktreeFolderOutOfTheMainCheckoutStatus()
    {
        _probe.AppendLine("README.md", "uncommitted work");

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 0, $"run start must exit 0 on a dirty checkout. {result.Output}");
        Assert.Single(Directory.GetDirectories(Path.Combine(_probe.Root, Folder)));
        GitOutcome status = _git.Run(_probe.Root, ["status", "--porcelain"]);
        Assert.True(status.Succeeded, status.FirstErrorLine);
        Assert.DoesNotContain(Folder, status.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A folder under a linked worktree is excluded by the exclude file git shares with the main checkout, so
    ///     the linked worktree's status does not list it.
    /// </summary>
    [Fact]
    public void AWorktreeFolderUnderALinkedWorktreeStaysOutOfItsStatus()
    {
        _probe.AddLinkedWorktree(LinkedBranch);
        string linked = LinkedWorktreePath();
        RunStartRunner runner = new(
            _git,
            new PhysicalFileSystem(),
            new ProcessEnvironment(),
            new PhysicalPathResolver(new PhysicalFileSystem()),
            linked);

        GitOutcome excluded = runner.EnsureWorktreeFolderExcluded();

        Assert.True(excluded.Succeeded, excluded.FirstErrorLine);
        string worktree = Path.Combine(linked, Folder, "20261118T010203Z-claude-fix-linked-exclude");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, "marker.txt"), "a worktree\n");
        GitOutcome status = _git.Run(linked, ["status", "--porcelain"]);
        Assert.True(status.Succeeded, status.FirstErrorLine);
        Assert.DoesNotContain(Folder, status.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            ".agent_worktrees/",
            File.ReadAllText(Path.Combine(_probe.Root, ".git", "info", "exclude")),
            StringComparison.Ordinal);
    }

    /// <summary>The path git reports for the worktree on the linked branch, found in the porcelain listing.</summary>
    private string LinkedWorktreePath()
    {
        foreach (string block in _probe.GitOutput("worktree", "list", "--porcelain").Split("\n\n"))
        {
            string[] lines = block.Split('\n');
            if (lines.Contains("branch refs/heads/" + LinkedBranch))
            {
                return lines.First(line => line.StartsWith("worktree ", StringComparison.Ordinal))["worktree ".Length..].Trim();
            }
        }

        throw new InvalidOperationException("git lists no worktree on the linked branch.");
    }
}
