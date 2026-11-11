using KyberWeave.Core.Doctor;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins where <c>doctor --ensure</c> writes the shared post-checkout hook for a clone and its worktrees, what it
/// refuses to touch, and how a disabled setting removes the block.
/// </summary>
/// <remarks>
/// These run real <c>git</c> in temporary repositories. The answers that matter are git's own: which hooks folder a
/// worktree resolves, what <c>core.hooksPath</c> points at, and whether a folder is tracked. A scripted answer would
/// only repeat the assumption the test was meant to check.
/// </remarks>
public sealed class PostCheckoutHookInstallTests : IDisposable
{
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode PlainMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly PostCheckoutGitFixture _git = new();

    public void Dispose() => _git.Dispose();

    [Fact]
    public void EnsureInstallsTheHookExecutableAndReportsItInstalled()
    {
        _git.CreateGovernedRepository();

        DoctorCheck? check = PostCheckoutHook.Ensure(_git.Repository, _git.Executor());

        string hook = PostCheckoutGitFixture.HookPath(_git.Repository);
        Assert.True(File.Exists(hook), "Ensure must write .git/hooks/post-checkout in a governed repository.");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(ExecutableMode, File.GetUnixFileMode(hook));
        }

        Assert.NotNull(check);
        Assert.Equal(PostCheckoutHook.CheckId, check.Id);
        Assert.True(check.Passed);
        Assert.Contains("installed", check.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A linked worktree has its own git directory, but the hook is shared, so the install must land in the common
    /// hooks folder. Writing under the worktree's own folder would leave every other worktree without the hook.
    /// </summary>
    [Fact]
    public void EnsureFromALinkedWorktreeWritesTheCommonHooksFolderNotTheWorktreeFolder()
    {
        _git.CreateGovernedRepository();
        _git.CommitAll(_git.Repository, "govern the repository");
        string worktree = Path.Combine(_git.Root, "linked");
        _git.GitOk(_git.Repository, "worktree", "add", "-b", "linked-branch", worktree);

        PostCheckoutHook.Ensure(worktree, _git.Executor());

        Assert.True(File.Exists(PostCheckoutGitFixture.HookPath(_git.Repository)));
        Assert.False(
            File.Exists(Path.Combine(_git.Repository, ".git", "worktrees", "linked", "hooks", "post-checkout")),
            "The hook must not be written under the worktree's own git directory.");
    }

    [Fact]
    public void EnsureWithAnUntrackedRelativeHooksPathWritesIntoThatFolder()
    {
        _git.CreateGovernedRepository();
        _git.GitOk(_git.Repository, "config", "core.hooksPath", "hooks-local");

        PostCheckoutHook.Ensure(_git.Repository, _git.Executor());

        Assert.True(File.Exists(Path.Combine(_git.Repository, "hooks-local", "post-checkout")));
        Assert.False(File.Exists(PostCheckoutGitFixture.HookPath(_git.Repository)));
    }

    /// <summary>
    /// A hooks folder that git tracks belongs to the repository, so a write there would show up as a change to commit.
    /// The install reports the problem and writes nothing.
    /// </summary>
    [Fact]
    public void EnsureWithATrackedHooksPathWritesNothingAndWarns()
    {
        _git.CreateGovernedRepository();
        string hooks = Path.Combine(_git.Repository, "githooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\nexit 0\n");
        _git.CommitAll(_git.Repository, "track the hooks folder");
        _git.GitOk(_git.Repository, "config", "core.hooksPath", "githooks");

        DoctorCheck? check = PostCheckoutHook.Ensure(_git.Repository, _git.Executor());

        Assert.False(File.Exists(Path.Combine(hooks, "post-checkout")));
        AssertWarning(check);
    }

    [Fact]
    public void EnsureLeavesAHookWithoutTheExecutableBitByteForByteAndWarns()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The executable bit is a Unix mode; Windows has no equivalent to refuse on.");
        }

        _git.CreateGovernedRepository();
        string hook = WriteHook("#!/bin/sh\necho mine\n", executable: false);
        byte[] before = File.ReadAllBytes(hook);

        DoctorCheck? check = PostCheckoutHook.Ensure(_git.Repository, _git.Executor());

        Assert.Equal(before, File.ReadAllBytes(hook));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(PlainMode, File.GetUnixFileMode(hook));
        }

        AssertWarning(check);
    }

    [Fact]
    public void EnsureLeavesAPythonHookByteForByteAndWarns()
    {
        _git.CreateGovernedRepository();
        string hook = WriteHook("#!/usr/bin/env python3\nprint('keep')\n", executable: true);
        byte[] before = File.ReadAllBytes(hook);

        DoctorCheck? check = PostCheckoutHook.Ensure(_git.Repository, _git.Executor());

        Assert.Equal(before, File.ReadAllBytes(hook));
        AssertWarning(check);
    }

    /// <summary>
    /// The setting is the owner's way to switch the hook off. An install that already put the block in must take it
    /// out again, and the owner's own lines must survive the removal.
    /// </summary>
    [Fact]
    public void EnsureWithWorktreeIndexHookFalseRemovesTheInstalledBlockAndKeepsOtherLines()
    {
        _git.CreateGovernedRepository();
        PostCheckoutHook.Ensure(_git.Repository, _git.Executor());
        string hook = PostCheckoutGitFixture.HookPath(_git.Repository);
        File.AppendAllText(hook, "echo keep-me\n");
        PostCheckoutGitFixture.WriteGoverned(_git.Repository, "stop-on-tool-failure: true\nworktree-index-hook: false\n");

        PostCheckoutHook.Ensure(_git.Repository, _git.Executor());

        Assert.True(File.Exists(hook), "The owner's own line remains, so the file must not be deleted.");
        string text = File.ReadAllText(hook);
        Assert.DoesNotContain(PostCheckoutHookText.Block, text, StringComparison.Ordinal);
        Assert.Contains("echo keep-me", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectWithoutTheHookReportsAWarningAndWritesNothing()
    {
        _git.CreateGovernedRepository();

        DoctorCheck? check = PostCheckoutHook.Inspect(_git.Repository, _git.Executor());

        Assert.False(File.Exists(PostCheckoutGitFixture.HookPath(_git.Repository)));
        AssertWarning(check);
    }

    [Fact]
    public void EnsureInARepositoryWithoutGovernedDocsReturnsNoCheckAndWritesNoFile()
    {
        _git.CreateRepository();

        DoctorCheck? check = PostCheckoutHook.Ensure(_git.Repository, _git.Executor());

        Assert.Null(check);
        Assert.False(File.Exists(PostCheckoutGitFixture.HookPath(_git.Repository)));
    }

    /// <summary>
    /// A hook problem is a passing check whose reason starts with <c>warning:</c>. A failed check would change the
    /// exit code of <c>doctor</c>, and a hook the owner wrote by hand must not stop a worker's run.
    /// </summary>
    private static void AssertWarning(DoctorCheck? check)
    {
        Assert.NotNull(check);
        Assert.Equal(PostCheckoutHook.CheckId, check.Id);
        Assert.True(check.Passed, "A hook problem is a passing check, so it can never change the exit code.");
        Assert.StartsWith("warning:", check.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    private string WriteHook(string text, bool executable)
    {
        string hook = PostCheckoutGitFixture.HookPath(_git.Repository);
        Directory.CreateDirectory(Path.Combine(_git.Repository, ".git", "hooks"));
        File.WriteAllText(hook, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, executable ? ExecutableMode : PlainMode);
        }

        return hook;
    }
}
