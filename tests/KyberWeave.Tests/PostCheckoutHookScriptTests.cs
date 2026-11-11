using System.Diagnostics;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Runs the installed post-checkout hook through a real <c>git worktree add</c>, with a stub <c>kyber-weave</c> on
/// PATH that records how it was called.
/// </summary>
/// <remarks>
/// <para>
/// The hook starts <c>kyber-weave doctor --post-checkout .</c> in the background, so a new worktree gets its index
/// without the checkout waiting for it. These tests pin the three things that make that safe: the background child
/// starts only on a new worktree, a missing or failing or slow child never fails the checkout, and an owner's own
/// shell hook keeps running.
/// </para>
/// <para>
/// The stub is a POSIX shell script, so the class is skipped on Windows, as <c>ArbiterShimBehaviourTests</c> is.
/// </para>
/// </remarks>
public sealed class PostCheckoutHookScriptTests : IDisposable
{
    private static readonly TimeSpan ChildWindow = TimeSpan.FromSeconds(10);

    private readonly PostCheckoutGitFixture _git = new();

    public void Dispose() => _git.Dispose();

    /// <summary>
    /// The marker shows the stub ran as <c>doctor --post-checkout .</c> in the new worktree, and the index log sits in
    /// that worktree's own git directory, where the next run will look for it.
    /// </summary>
    [Fact]
    public async Task ANewWorktreeCheckoutStartsDoctorPostCheckoutInTheNewWorktreeRoot()
    {
        RequirePosix();
        InstallBlockOnGovernedRepository();
        WriteStub(string.Empty);
        string worktree = Path.Combine(_git.Root, "fresh");

        _git.GitOnOk(_git.Repository, WithStub(), "worktree", "add", "-b", "fresh-branch", worktree);

        string invocations = await WaitForInvocationAsync("doctor --post-checkout .");
        string cwd = CwdOf(invocations);
        Assert.Equal(Path.GetFileName(worktree), Path.GetFileName(cwd.TrimEnd('/')));
        string gitDirectory = _git.GitOutput(worktree, "rev-parse", "--absolute-git-dir").Trim();
        Assert.True(
            File.Exists(Path.Combine(gitDirectory, "kyber-weave-index.log")),
            $"The index log must sit in the worktree's git directory: {gitDirectory}");
    }

    /// <summary>
    /// A branch switch in a tree that already exists is not a new worktree. The hook must do nothing, so the switch
    /// may not start a second index run. The switch is followed by a real new worktree so the wait has a marker to
    /// end on, and the test counts the invocations that came from the switch.
    /// </summary>
    [Fact]
    public async Task SwitchingBranchesInAnExistingTreeNeverStartsDoctor()
    {
        RequirePosix();
        InstallBlockOnGovernedRepository();
        WriteStub(string.Empty);
        _git.GitOk(_git.Repository, "branch", "other");

        _git.GitOnOk(_git.Repository, WithStub(), "switch", "other");
        string worktree = Path.Combine(_git.Root, "after-switch");
        _git.GitOnOk(_git.Repository, WithStub(), "worktree", "add", "-b", "after-switch", worktree);

        string leaf = Path.GetFileName(worktree);
        string invocations = await WaitUntilAsync(text => ParseRecords(text).Any(record => record.Cwd == leaf));
        IReadOnlyList<InvocationRecord> records = ParseRecords(invocations);
        Assert.All(records, record => Assert.Equal(leaf, record.Cwd));
        Assert.Single(records, record => record.Args.StartsWith("doctor --post-checkout .", StringComparison.Ordinal));
    }

    /// <summary>
    /// A machine without kyber-weave on PATH must not see the checkout fail. The block checks for the command first
    /// and does nothing when it is absent.
    /// </summary>
    [Fact]
    public void WithNoStubOnPathWorktreeAddExitsZeroWithNoHookError()
    {
        RequirePosix();
        InstallBlockOnGovernedRepository();
        string worktree = Path.Combine(_git.Root, "bare");

        ProcessResult result = _git.GitOn(
            _git.Repository,
            PostCheckoutGitFixture.PathStartingWith(_git.EmptyBin),
            "worktree", "add", "-b", "bare-branch", worktree);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("hook", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A failing index run is a failed background child. The checkout has already happened, so its exit code stays 0.
    /// </summary>
    [Fact]
    public async Task AStubThatExitsOneLeavesWorktreeAddAtExitZero()
    {
        RequirePosix();
        InstallBlockOnGovernedRepository();
        WriteStub("exit 1");
        string worktree = Path.Combine(_git.Root, "failing");

        ProcessResult result = _git.GitOn(
            _git.Repository,
            WithStub(),
            "worktree", "add", "-b", "failing-branch", worktree);

        Assert.Equal(0, result.ExitCode);
        await WaitForInvocationAsync("doctor --post-checkout .");
    }

    /// <summary>
    /// The index run takes a long time, and the checkout must not wait for it. The stub sleeps, and the pipes git
    /// reads stay open for as long as any background child holds them. Only a hook that detaches its child, with
    /// stdin, stdout and stderr redirected, lets the checkout return within the window.
    /// </summary>
    [Fact]
    public async Task ASlowStubStillLetsWorktreeAddReturnWithinTenSeconds()
    {
        RequirePosix();
        InstallBlockOnGovernedRepository();
        WriteStub("exec sleep 30");
        string worktree = Path.Combine(_git.Root, "slow");

        Stopwatch clock = Stopwatch.StartNew();
        ProcessResult result = _git.GitOn(
            _git.Repository,
            WithStub(),
            "worktree", "add", "-b", "slow-branch", worktree);
        clock.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.True(
            clock.Elapsed < ChildWindow,
            $"worktree add took {clock.Elapsed.TotalSeconds:F1}s; a background index run held the checkout open.");
        await WaitForInvocationAsync("doctor --post-checkout .");
    }

    /// <summary>
    /// An owner's own <c>#!/bin/sh -eu</c> hook ends in <c>exit 0</c>. The block goes in directly after the shebang,
    /// so it runs first, the owner's body still runs, and the checkout still exits 0.
    /// </summary>
    [Fact]
    public async Task AnExistingShellHookWithSetEuKeepsWorkingAndStillStartsDoctor()
    {
        RequirePosix();
        _git.CreateGovernedRepository();
        _git.CommitAll(_git.Repository, "govern the repository");
        string ownMarker = Path.Combine(_git.Markers, "own-hook-ran");
        WriteHook($"#!/bin/sh -eu\necho own >> '{ownMarker}'\nexit 0\n");
        PostCheckoutHook.Ensure(_git.Repository, _git.Executor());
        WriteStub(string.Empty);
        string worktree = Path.Combine(_git.Root, "owned");

        ProcessResult result = _git.GitOn(
            _git.Repository,
            WithStub(),
            "worktree", "add", "-b", "owned-branch", worktree);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(ownMarker), "The owner's own hook body must still run.");
        await WaitForInvocationAsync("doctor --post-checkout .");
    }

    /// <summary>Creates a governed repository, commits it, and installs the block the way <c>doctor --ensure</c> does.</summary>
    private void InstallBlockOnGovernedRepository()
    {
        _git.CreateGovernedRepository();
        _git.CommitAll(_git.Repository, "govern the repository");
        PostCheckoutHook.Ensure(_git.Repository, _git.Executor());
    }

    /// <summary>
    /// Writes the stub <c>kyber-weave</c>. It appends its arguments and working directory to the invocation record, and
    /// appends its PID first, so the fixture can kill it if the test ends while it still runs.
    /// </summary>
    /// <param name="tail">Shell lines that run after the record is written, such as an exit or a sleep.</param>
    private void WriteStub(string tail)
    {
        string script = $$"""
            #!/bin/sh
            echo $$ >> '{{_git.Markers}}/pids'
            {
              printf 'args='
              for arg in "$@"; do printf '%s ' "$arg"; done
              printf '\n'
              printf 'cwd=%s\n' "$(pwd -P)"
            } >> '{{_git.Markers}}/invocations'
            {{tail}}
            """;
        string path = Path.Combine(_git.Stubs, "kyber-weave");
        File.WriteAllText(path, script);
        MakeExecutable(path);
    }

    private void WriteHook(string text)
    {
        string hook = PostCheckoutGitFixture.HookPath(_git.Repository);
        Directory.CreateDirectory(Path.Combine(_git.Repository, ".git", "hooks"));
        File.WriteAllText(hook, text);
        MakeExecutable(hook);
    }

    /// <summary>Sets the owner-executable bit on a POSIX script. Only reached after <see cref="RequirePosix"/>.</summary>
    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Waits up to ten seconds for the stub's record to name <paramref name="needle"/>, then returns the record.</summary>
    private Task<string> WaitForInvocationAsync(string needle) =>
        WaitUntilAsync(text => text.Contains(needle, StringComparison.Ordinal));

    /// <summary>
    /// Waits up to ten seconds for the invocation record to satisfy <paramref name="ready"/>, then returns the record.
    /// </summary>
    private async Task<string> WaitUntilAsync(Func<string, bool> ready)
    {
        string record = Path.Combine(_git.Markers, "invocations");
        DateTime deadline = DateTime.UtcNow.Add(ChildWindow);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(record))
            {
                string text = await File.ReadAllTextAsync(record);
                if (ready(text))
                {
                    return text;
                }
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The kyber-weave invocation record did not reach the expected state within {ChildWindow.TotalSeconds} seconds.");
        return string.Empty;
    }

    /// <summary>One stub invocation: its arguments and the leaf name of the working directory it ran in.</summary>
    private sealed record InvocationRecord(string Args, string Cwd);

    /// <summary>Pairs each <c>args=</c> line of the record with the <c>cwd=</c> line that follows it.</summary>
    private static IReadOnlyList<InvocationRecord> ParseRecords(string text)
    {
        List<InvocationRecord> records = [];
        string? args = null;
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("args=", StringComparison.Ordinal))
            {
                args = line["args=".Length..];
            }
            else if (line.StartsWith("cwd=", StringComparison.Ordinal) && args is not null)
            {
                records.Add(new InvocationRecord(args, Path.GetFileName(line["cwd=".Length..].TrimEnd('/'))));
                args = null;
            }
        }

        return records;
    }

    private static string CwdOf(string invocations)
    {
        string? line = invocations.Split('\n').FirstOrDefault(candidate => candidate.StartsWith("cwd=", StringComparison.Ordinal));
        Assert.NotNull(line);
        return line["cwd=".Length..];
    }

    /// <summary>The PATH a git child sees: the stub folder first, so the hook finds the stub and nothing else.</summary>
    private string WithStub() => PostCheckoutGitFixture.PathStartingWith(_git.Stubs);

    private static void RequirePosix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The stub kyber-weave is a POSIX shell script.");
        }
    }
}
