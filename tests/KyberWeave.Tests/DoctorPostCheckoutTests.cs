using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins <c>doctor --post-checkout</c>, the entry the git hook starts in the background on a new worktree: it indexes
/// the worktree, restores only what <c>codegraph init</c> wrote, never starts the MCP server, and never blocks a run.
/// </summary>
/// <remarks>
/// The hook fires on every new checkout, including ones where a person has uncommitted work. So this path must not
/// rewind an edit it did not cause, and the tests use the scripted executor the ensure tests use, so no child spawns.
/// </remarks>
public sealed class DoctorPostCheckoutTests : IDisposable
{
    private const string EditedFile = "src/edited.cs";
    private const string RestoreCheckId = "codegraph.restore";

    private readonly TempDirectory _root = new();
    private readonly TempDirectory _stubs = new();
    private readonly TempDirectory _locks = new();

    public void Dispose()
    {
        _locks.Dispose();
        _stubs.Dispose();
        _root.Dispose();
    }

    private string Root => _root.Path;

    /// <summary>
    /// With the setting off, a new worktree gets no index run at all. The hook is still installed, so the switch is the
    /// only control the owner needs.
    /// </summary>
    [Fact]
    public void PostCheckoutWithWorktreeIndexHookFalseStartsNoCodeGraphChildAndExitsZero()
    {
        WriteGoverned("worktree-index-hook: false\n");
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        DoctorReport report = RepositoryDoctor.RunPostCheckout(Root, DoctorHarness.Options(Mcp()), executor, _locks.Path);

        Assert.Empty(executor.CallsTo("codegraph"));
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// The restore reverts exactly the tracked files <c>codegraph init</c> writes, which is the list the provisioner
    /// names. An edit the worker made, here <see cref="EditedFile"/>, stays in place and is named as left alone.
    /// </summary>
    /// <remarks>
    /// The list is empty today because a fresh clone showed <c>codegraph init</c> writes no tracked file, so the restore
    /// set is empty and only the naming of the edited file is observable. The assertion is against the constant, so a
    /// future CodeGraph release that writes a tracked file is restored by name without editing this test.
    /// </remarks>
    [Fact]
    public void PostCheckoutRestoresOnlyTheInitConfigFilesAndNamesEveryOtherDirtyFileAsLeftAlone()
    {
        WriteGoverned("stop-on-tool-failure: true\n");
        bool initialised = false;
        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, Root + "\n", string.Empty);
                }

                if (arguments.Contains("status", StringComparison.Ordinal)
                    || arguments.Contains("diff", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, initialised ? DirtyAfterInit() : string.Empty, string.Empty);
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
            {
                if (startInfo.ArgumentList.Contains("init", StringComparer.Ordinal))
                {
                    initialised = true;
                }

                return DoctorHarness.Ok();
            }

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        DoctorReport report = RepositoryDoctor.RunPostCheckout(Root, DoctorHarness.Options(Mcp()), executor, _locks.Path);

        string[] restored = executor.CallsTo("git")
            .Where(call => call.ArgumentList.Contains("restore", StringComparer.Ordinal))
            .SelectMany(call => call.ArgumentList.SkipWhile(argument => argument != "--").Skip(1))
            .ToArray();
        Assert.Equal(
            CodeGraphProvisioner.InitConfigFiles.Order(StringComparer.Ordinal),
            restored.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(EditedFile, restored, StringComparer.Ordinal);

        DoctorCheck restore = Assert.Single(report.Checks, check => check.Id == RestoreCheckId);
        Assert.True(restore.Passed);
        Assert.Contains(EditedFile, restore.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// The MCP server is the docs probe the ensure run performs. A new worktree needs only the index, so the hook path
    /// must never start that server. The stub records its own start, and the test checks it never ran.
    /// </summary>
    [Fact]
    public void PostCheckoutNeverStartsTheMcpExecutable()
    {
        WriteGoverned("stop-on-tool-failure: true\n");
        string startedRecord = Path.Combine(_stubs.Path, "mcp-started.txt");
        string mcp = DoctorHarness.RecordingStub(_stubs.Path, Root, startedRecord);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        RepositoryDoctor.RunPostCheckout(Root, DoctorHarness.Options(mcp), executor, _locks.Path);

        Assert.False(File.Exists(startedRecord), "The MCP server must not start on the post-checkout path.");
        Assert.DoesNotContain(executor.Calls, call => call.FileName == mcp);
    }

    /// <summary>
    /// A repository with no governed docs has no index to keep, so the hook path starts nothing for it.
    /// </summary>
    [Fact]
    public void PostCheckoutInARepositoryWithoutGovernedDocsStartsNoCodeGraphChild()
    {
        using TempDirectory bare = new();
        ScriptedProcessExecutor executor = DoctorHarness.Executor(bare.Path);

        RepositoryDoctor.RunPostCheckout(bare.Path, DoctorHarness.Options(Mcp()), executor, _locks.Path);

        Assert.Empty(executor.CallsTo("codegraph"));
    }

    /// <summary>
    /// <c>--post-checkout</c> is the hook's own mode. Combined with <c>--ensure</c> it is a usage error, exit 2, so an
    /// owner typing both gets a refusal rather than a run that does one thing and reports the other.
    /// </summary>
    [Fact]
    public void PostCheckoutCombinedWithEnsureIsAUsageErrorExitingTwo()
    {
        WriteGoverned("stop-on-tool-failure: true\n");
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);
        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => false,
            ReadEnvironment = _ => null,
            McpExecutable = Mcp()
        };

        int exitCode = DoctorAppHarness.Run(command, "doctor", "--post-checkout", "--ensure", Root);

        Assert.Equal(2, exitCode);
        Assert.Empty(executor.CallsTo("codegraph"));
    }

    /// <summary>
    /// The hook's status is reported on the ensure run, so an owner sees a broken hook without a separate command. A
    /// warning in it is a passing check, so the exit code of the whole ensure run is unchanged.
    /// </summary>
    [Fact]
    public void EnsureOnAGovernedRootReportsOneHookCheckAndAHookWarningKeepsTheExitCode()
    {
        WriteGoverned("stop-on-tool-failure: true\n");
        string hooks = Path.Combine(Root, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "post-checkout"), "#!/usr/bin/env python3\nprint('mine')\n");
        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("--git-path", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, hooks + "\n", string.Empty);
                }

                if (arguments.Contains("--show-toplevel", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, Root + "\n", string.Empty);
                }

                if (arguments.Contains("hooksPath", StringComparison.Ordinal))
                {
                    return new ProcessResult(1, string.Empty, string.Empty);
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
            {
                return DoctorHarness.Ok();
            }

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(Mcp()),
            executor,
            _ => null,
            _locks.Path);

        DoctorCheck hook = Assert.Single(report.Checks, check => check.Id == PostCheckoutHook.CheckId);
        Assert.True(hook.Passed);
        Assert.StartsWith("warning:", hook.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, report.ExitCode);
    }

    private string Mcp() => DoctorHarness.HealthyStub(_stubs.Path, Root);

    private string DirtyAfterInit() =>
        " M " + EditedFile + "\n"
        + string.Concat(CodeGraphProvisioner.InitConfigFiles.Select(file => " M " + file + "\n"));

    private void WriteGoverned(string config)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        File.WriteAllText(Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"), config);
        Directory.CreateDirectory(Path.Combine(Root, "docs"));
    }
}
