using Xunit;
using System.Diagnostics;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the repository doctor's CodeGraph side (D2, D3, D6, D10, D11) and its refusal cases. The MCP side is in
/// <see cref="McpLiveProbeTests"/>. Each test owns its temp repository and its stub directory.
/// </summary>
public sealed class RepositoryDoctorTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly TempDirectory _stubs = new();

    public void Dispose()
    {
        _stubs.Dispose();
        _root.Dispose();
    }

    private string Root => _root.Path;

    private string IndexDirectory => Path.Combine(Root, ".codegraph");

    /// <summary>A missing CodeGraph binary stops the run with exit 3 and a fix, and nothing is provisioned.</summary>
    /// <remarks>
    /// Exit 3 separates "ask the human to install" from "something broke", so the skill and run-start can branch
    /// on the code. Without this, a doctor that tried to install or init on a missing binary would be silent about it.
    /// </remarks>
    [Fact]
    public void Doctor_CodegraphMissing_Exits3WithInstallCommand_AndRunsNothing()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root, _ => throw DoctorHarness.BinaryNotFound());

        DoctorReport report = RepositoryDoctor.Run(
            Root, DoctorMode.Provision, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, _ => null);

        Assert.Equal(3, report.ExitCode);
        DoctorCheck binary = DoctorHarness.CheckNamed(report, "codegraph.binary");
        Assert.False(binary.Passed);
        Assert.False(string.IsNullOrWhiteSpace(binary.Fix));
        DoctorHarness.AssertNoCall(executor, "init");
        DoctorHarness.AssertNoCall(executor, "index");
    }

    /// <summary>A provisioning run with no index runs <c>codegraph init</c> at the repository root, not <c>index</c>.</summary>
    /// <remarks>D3 and D10: a missing <c>.codegraph/</c> is initialised, and the init runs with cwd = root.</remarks>
    [Fact]
    public void Doctor_Provision_NoIndex_RunsInitThenSucceeds()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Provision,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)),
            executor,
            _ => null);

        ProcessStartInfo init = Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        Assert.Equal(Root, init.WorkingDirectory);
        DoctorHarness.AssertNoCall(executor, "index");
        Assert.True(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>An existing <c>.codegraph/</c> is incrementally indexed, never re-initialised.</summary>
    /// <remarks>D3 "always index" on provisioning: <c>index</c> is the cheap path once an index exists.</remarks>
    [Fact]
    public void Doctor_Provision_IndexPresent_RunsIndex()
    {
        Directory.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Provision,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)),
            executor,
            _ => null);

        Assert.Contains(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("index", StringComparer.Ordinal));
        DoctorHarness.AssertNoCall(executor, "init");
        Assert.True(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
    }

    /// <summary>
    /// An index that exceeds the timeout fails the check with exit 1. The tree kill itself happens in the process
    /// executor, which this unit test cannot observe.
    /// </summary>
    /// <remarks>
    /// The executor surfaces a timeout as <see cref="TimeoutException"/>, the production contract of
    /// <c>ProcessExecutor</c>. Without the failure path, a hung index would hang the doctor or pass silently.
    /// </remarks>
    [Fact]
    public void Doctor_Provision_IndexTimeout_KillsTreeExits1()
    {
        Directory.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("index", StringComparer.Ordinal)
                ? throw new TimeoutException("codegraph index exceeded its timeout.")
                : DoctorHarness.Ok());

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Provision,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)),
            executor,
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
    }

    /// <summary>
    /// <c>--check</c> is read-only. It never runs <c>init</c> or <c>index</c>, even when there is no index at all,
    /// and a missing index is reported as a failure.
    /// </summary>
    /// <remarks>
    /// D11: run-start and CI call <c>--check</c> on every run, so a check that quietly indexed would pay a full index
    /// on every run and turn a read-only gate into a write.
    /// </remarks>
    [Fact]
    public void Doctor_Check_NeverInvokesInitOrIndex()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)),
            executor,
            _ => null);

        DoctorHarness.AssertNoCall(executor, "init");
        DoctorHarness.AssertNoCall(executor, "index");
        Assert.False(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
        Assert.Equal(1, report.ExitCode);
    }

    /// <summary>
    /// Bare <c>doctor</c> with no TTY, or with <c>CI</c> set, acts as <c>--check</c>. The TTY and <c>CI</c> resolution
    /// belongs to the CLI command, and <see cref="RepositoryDoctor.Run"/> takes no TTY seam.
    /// </summary>
    /// <remarks>
    /// Skipped, not weakened. The contract's <c>Run</c> signature takes a mode and no TTY, so this cannot be expressed
    /// against the Core seam. It needs a <c>DoctorCommand</c> seam for stdin-is-TTY and the environment (D11).
    /// </remarks>
    [Fact]
    public void Doctor_NonInteractive_Bare_BehavesAsCheck_PrintsHint()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);
        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => false,
            ReadEnvironment = name => name == "CI" ? "true" : null,
            McpExecutable = "kyber-weave-mcp-absent",
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            command.Execute(null!, new DoctorSettings { Path = Root }, CancellationToken.None));

        Assert.Contains("provisioning skipped: non-interactive; rerun with --provision", execution.Output, StringComparison.Ordinal);
        DoctorHarness.AssertNoCall(executor, "init");
        DoctorHarness.AssertNoCall(executor, "index");
    }

    /// <summary>A root that is not a git repository refuses with exit 2 and provisions nothing.</summary>
    [Fact]
    public void Doctor_RootIsNotGit_Exits2()
    {
        ScriptedProcessExecutor executor = new(startInfo =>
            startInfo.FileName == "git"
                ? new ProcessResult(128, string.Empty, "fatal: not a git repository")
                : throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."));

        DoctorReport report = RepositoryDoctor.Run(
            Root, DoctorMode.Provision, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, _ => null);

        Assert.Equal(2, report.ExitCode);
        Assert.DoesNotContain(executor.Calls, call => call.FileName == "codegraph");
    }

    /// <summary>
    /// A root equal to <c>$HOME</c> refuses with exit 2. Provisioning there would index the whole home directory,
    /// so it is refused before any child runs.
    /// </summary>
    [Fact]
    public void Doctor_RootIsHome_Exits2()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Provision,
            DoctorHarness.Options("kyber-weave-mcp-absent"),
            executor,
            name => name == "HOME" ? Root : null);

        Assert.Equal(2, report.ExitCode);
        Assert.DoesNotContain(executor.Calls, call => call.FileName == "codegraph");
    }

    /// <summary>
    /// A second doctor on a root another doctor holds waits up to the index timeout and then exits 1 with
    /// <c>codegraph.index</c> busy.
    /// </summary>
    /// <remarks>
    /// Skipped, not weakened. D10 places the lock file in the OS temp directory keyed by a hash of the root, but the
    /// contract fixes no lock seam and no path, so a test cannot hold the lock. It needs the provisioner's lock path
    /// exposed or injected.
    /// </remarks>
    [Fact]
    public void Doctor_ConcurrentLockBusy_Exits1Busy()
    {
        Directory.CreateDirectory(IndexDirectory);
        string lockDirectory = Path.GetTempPath();
        string lockPath = CodeGraphProvisioner.LockPathFor(Root, lockDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            DoctorReport report = RepositoryDoctor.Run(
                Root,
                DoctorMode.Provision,
                new DoctorOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), "codegraph", "kyber-weave-mcp-absent"),
                executor,
                _ => null,
                lockDirectory);

            Assert.Equal(1, report.ExitCode);
            DoctorCheck index = DoctorHarness.CheckNamed(report, "codegraph.index");
            Assert.False(index.Passed);
            Assert.Contains("busy", index.Reason, StringComparison.Ordinal);
            DoctorHarness.AssertNoCall(executor, "index");
        }
    }

    /// <summary>
    /// An unwritable <c>.codegraph/</c> fails with exit 1 and a remedy, and is never deleted. The directory is made
    /// read-only, so the test restores its mode before the temp directory is removed.
    /// </summary>
    /// <remarks>
    /// D10: doctor never deletes a user's index, because a deletion would silently throw away a large index. The remedy
    /// is for a human to run, not for doctor to do.
    /// </remarks>
    [Fact]
    public void Doctor_CodegraphDirUnwritable_Exits1WithRemedy_DoesNotDelete()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX directory modes decide this case; Windows has no equivalent read-only directory bit.");
        }

        Directory.CreateDirectory(IndexDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(IndexDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

            DoctorReport report = RepositoryDoctor.Run(
                Root,
                DoctorMode.Provision,
                DoctorHarness.Options("kyber-weave-mcp-absent"),
                executor,
                _ => null);

            Assert.Equal(1, report.ExitCode);
            DoctorCheck index = DoctorHarness.CheckNamed(report, "codegraph.index");
            Assert.False(index.Passed);
            Assert.False(string.IsNullOrWhiteSpace(index.Fix));
            Assert.True(Directory.Exists(IndexDirectory), "The doctor must never delete .codegraph/.");
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    IndexDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
