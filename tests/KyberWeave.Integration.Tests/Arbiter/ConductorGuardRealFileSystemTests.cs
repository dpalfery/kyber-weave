using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Arbiter;
using Xunit;

namespace KyberWeave.Integration.Tests.Arbiter;

/// <summary>
/// What the Copilot conductor guard reads from the real file system and real git: a symbolic link on a contract path,
/// and the canonical checkout of a run's own worktree. Neither can be shown over an in-memory file system.
/// </summary>
public sealed class ConductorGuardRealFileSystemTests
{
    private const string CopilotHome = "/home/me/.copilot";

    [Fact]
    public void AContractPathThroughASymlink_IsDenied()
    {
        string root = Directory.CreateTempSubdirectory("guard-link-").FullName;
        try
        {
            string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "secret.md"), "x");
            string repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            Directory.CreateDirectory(Path.Combine(repo, "docs", "plans"));
            Directory.CreateSymbolicLink(Path.Combine(repo, "docs", "plans", "link"), outside);
            File.WriteAllText(Path.Combine(repo, "docs", "plans", "real.md"), "x");

            Assert.Contains("deny", RunIn(repo, "docs/plans/link/secret.md"), StringComparison.Ordinal);
            Assert.Empty(RunIn(repo, "docs/plans/real.md"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task APlanInTheRunsOwnWorktree_IsPermittedFromTheCanonicalCheckout()
    {
        using TempDirectory parent = new();
        string repo = Path.Combine(parent.Path, "repo");
        string other = Path.Combine(parent.Path, "other");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(Path.Combine(other, "docs", "plans"));
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "-c", "user.email=a@example.com", "-c", "user.name=A", "commit", "-q", "--allow-empty", "-m", "initial");
        string worktree = Path.Combine(parent.Path, "repo.worktrees", "run");
        await Git(repo, "worktree", "add", "-q", "-b", "claude/feat/run", worktree);
        string root = GitRoot(worktree);
        Directory.CreateDirectory(Path.Combine(root, "docs", "plans"));

        Assert.Empty(RunIn(repo, Path.Combine(root, "docs", "plans", "2026-11-13-widget.md")));
        Assert.Empty(RunIn(repo, Path.Combine(root, "docs", "plans", "2026-11-13-widget.tasks.yml")));
        Assert.NotEmpty(RunIn(repo, Path.Combine(root, "src", "App.cs")));
        Assert.NotEmpty(RunIn(repo, Path.Combine(other, "docs", "plans", "x.md")));
    }

    private static string GitRoot(string directory) =>
        RealGitFacts.Create().GetRoot(directory) ?? throw new InvalidOperationException("no git root");

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
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }

    private static string RunIn(string cwd, string path, string? home = null)
    {
        using StringWriter stdout = new();
        ArbiterTestServices.Get<IConductorGuard>().Run(
            JsonSerializer.Serialize(new { cwd, toolName = "view", toolArgs = new { path } }),
            stdout,
            TextWriter.Null,
            copilotHome: CopilotHome,
            homeDirectory: home);
        return stdout.ToString();
    }
}
