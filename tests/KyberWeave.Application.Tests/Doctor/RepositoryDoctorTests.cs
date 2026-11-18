using System.Diagnostics;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests;
using KyberWeave.Tests.Doctor;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Doctor;

/// <summary>
/// Pins the repository doctor's CodeGraph side (D2, D3, D6, D10, D11) and its refusal cases. The MCP side is in
/// <see cref="McpServerLiveProbeTests"/>. Each test owns its in-memory repository; the cases that need a real lock
/// file or a real directory mode are in <c>RepositoryDoctorRealFileSystemTests</c>.
/// </summary>
public sealed class RepositoryDoctorTests
{
    private const string McpExecutable = "kyber-weave-mcp";

    private readonly InMemoryDoctorWorld _world = new();

    private string Root => _world.Root;

    private string IndexDirectory => Path.Combine(Root, ".codegraph");

    /// <summary>The options for a healthy MCP server: scripts one healthy session and names its executable.</summary>
    private DoctorOptions HealthyOptions()
    {
        _world.HealthyMcp(Root);
        return DoctorHarness.Options(McpExecutable);
    }

    /// <summary>A missing CodeGraph binary stops the run with exit 3 and a fix, and nothing is provisioned.</summary>
    /// <remarks>
    /// Exit 3 separates "ask the human to install" from "something broke", so the skill and run-start can branch
    /// on the code. Without this, a doctor that tried to install or init on a missing binary would be silent about it.
    /// </remarks>
    [Fact]
    public void Doctor_CodegraphMissing_Exits3WithInstallCommand_AndRunsNothing()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root, _ => throw DoctorHarness.BinaryNotFound());

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, _ => null, _world.Locks);

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

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, HealthyOptions(), executor, _ => null, _world.Locks);

        ProcessStartInfo init = Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        Assert.Equal(Root, init.WorkingDirectory);
        DoctorHarness.AssertNoCall(executor, "index");
        Assert.True(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>An existing <c>.codegraph/</c> is brought up to date with <c>sync</c>, never re-initialised.</summary>
    /// <remarks>
    /// D22: provisioning runs <c>sync</c> when an index exists and <c>init</c> when it does not. <c>index</c> is the
    /// repair step for a failed <c>sync</c>, never the routine one: re-indexing after a successful sync would rebuild
    /// the whole index on every provision for no new information, which is the cost this slice removes.
    /// </remarks>
    [Fact]
    public void Doctor_Provision_IndexPresent_RunsSync()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, HealthyOptions(), executor, _ => null, _world.Locks);

        Assert.Contains(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("sync", StringComparer.Ordinal));
        DoctorHarness.AssertNoCall(executor, "init");
        Assert.True(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
    }

    /// <summary>
    /// A <c>sync</c> that exceeds the timeout fails the check with exit 1. The tree kill itself happens in the process
    /// executor, which this unit test cannot observe.
    /// </summary>
    /// <remarks>
    /// The executor surfaces a timeout as <see cref="TimeoutException"/>, the production contract of
    /// <c>ProcessExecutor</c>. Without the failure path, a hung sync would hang the doctor or pass silently.
    /// </remarks>
    [Fact]
    public void Doctor_Provision_IndexTimeout_KillsTreeExits1()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("sync", StringComparer.Ordinal)
                ? throw new TimeoutException("codegraph sync exceeded its timeout.")
                : DoctorHarness.Ok());

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, HealthyOptions(), executor, _ => null, _world.Locks);

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

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Check, HealthyOptions(), executor, _ => null, _world.Locks);

        DoctorHarness.AssertNoCall(executor, "init");
        DoctorHarness.AssertNoCall(executor, "index");
        Assert.False(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
        Assert.Equal(1, report.ExitCode);
    }

    /// <summary>
    /// Bare <c>doctor</c> with no TTY, or with <c>CI</c> set, acts as <c>--check</c>. The TTY and <c>CI</c> resolution
    /// belongs to the CLI command, and <c>IRepositoryDoctor.Run</c> takes no TTY seam.
    /// </summary>
    /// <remarks>
    /// Skipped, not weakened. The contract's <c>Run</c> signature takes a mode and no TTY, so this cannot be expressed
    /// against the Core seam. It needs a <c>DoctorCommand</c> seam for stdin-is-TTY and the environment (D11).
    /// </remarks>
    [Fact]
    public void Doctor_NonInteractive_Bare_BehavesAsCheck_PrintsHint()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);
        DoctorCommand command = _world.Command(false, executor, "kyber-weave-mcp-absent");
        _ = command.Execute(null!, new DoctorSettings { Path = Root }, CancellationToken.None);

        Assert.Contains("provisioning skipped: non-interactive; rerun with --provision", _world.Console.Output, StringComparison.Ordinal);
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

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, _ => null, _world.Locks);

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

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, name => name == "HOME" ? Root : null, _world.Locks);

        Assert.Equal(2, report.ExitCode);
        Assert.DoesNotContain(executor.Calls, call => call.FileName == "codegraph");
    }

    [Fact]
    public void Doctor_BlankHome_DoesNotCrashAndDoesNotRefuse()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Check, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, _ => string.Empty, _world.Locks);

        Assert.NotEqual(2, report.ExitCode);
        Assert.NotEmpty(report.Checks);
    }

    /// <summary>
    /// A <c>git</c> child that exceeds its budget is a refusal with exit 2, not an exception out of the doctor. The
    /// executor surfaces a timeout as <see cref="TimeoutException"/>, the production contract of <c>ProcessExecutor</c>.
    /// </summary>
    [Fact]
    public void Doctor_GitTimesOut_Exits2WithReason()
    {
        ScriptedProcessExecutor executor = new(startInfo =>
            startInfo.FileName == "git"
                ? throw new TimeoutException("git exceeded its timeout.")
                : throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."));

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Check, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, _ => null, _world.Locks);

        Assert.Equal(2, report.ExitCode);
        DoctorCheck root = DoctorHarness.CheckNamed(report, "repository.root");
        Assert.False(root.Passed);
        Assert.Contains("git", root.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(executor.Calls, call => call.FileName == "codegraph");
    }

    /// <summary>
    /// A binary that exists but exits non-zero for <c>--version</c> is a broken binary, not a healthy one and not a
    /// missing one: the check fails, the run exits 1, and nothing is provisioned. Only exit 3 asks the user to install.
    /// </summary>
    [Fact]
    public void Doctor_CodegraphVersionExitsNonZero_FailsBinaryCheck()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("--version", StringComparer.Ordinal)
                ? new ProcessResult(2, string.Empty, "error: unknown option '--version'")
                : DoctorHarness.Ok());

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, HealthyOptions(), executor, _ => null, _world.Locks);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "codegraph.binary").Passed);
        DoctorHarness.AssertNoCall(executor, "index");
    }

    /// <summary>
    /// A process start that fails with an I/O error while probing the binary fails the binary check rather than
    /// escaping. The start can fail for reasons other than "not found", which is the only case that needs approval.
    /// </summary>
    [Fact]
    public void Doctor_CodegraphVersionThrowsIo_FailsBinaryCheck()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            _ => throw new IOException("The pipe has been ended."));

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, _ => null, _world.Locks);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "codegraph.binary").Passed);
    }

    /// <summary>
    /// A child that cannot start between the probe and the provisioning run fails <c>codegraph.index</c> with exit 1.
    /// The binary was on PATH a moment ago, so this is a broken environment, not a missing prerequisite.
    /// </summary>
    [Fact]
    public void Doctor_ProvisionChildFailsToStart_Exits1WithCheck()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("--version", StringComparer.Ordinal)
                ? DoctorHarness.Ok()
                : throw new IOException("The child process could not be started."));

        DoctorReport report = _world.Doctor.Run(Root, DoctorMode.Provision, DoctorHarness.Options("kyber-weave-mcp-absent"), executor, _ => null, _world.Locks);

        Assert.Equal(1, report.ExitCode);
        DoctorCheck index = DoctorHarness.CheckNamed(report, "codegraph.index");
        Assert.False(index.Passed);
        Assert.False(string.IsNullOrWhiteSpace(index.Fix));
    }

    [Fact]
    public void Doctor_BlankPath_Exits2()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);
        DoctorCommand command = _world.Command(false, executor, "kyber-weave-mcp-absent");
        int exitCode = command.Execute(null!, new DoctorSettings { Path = string.Empty }, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void Doctor_Ensure_MakesOneExploreCallAndOneDocsCall()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        _world.HealthyMcp(Root);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        int exitCode = DoctorAppHarness.Run(
            _world.Command(false, executor, McpExecutable),
            "doctor",
            "--ensure",
            Root);

        Assert.Equal(0, exitCode);
        Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("explore", StringComparer.Ordinal));
        Assert.Single(_world.Mcp.Sent, line => line.Contains("tools/call", StringComparison.Ordinal));
    }

    /// <summary>
    /// A docs stub that records one line per <c>tools/call</c> it receives, so a second probe is
    /// visible rather than overwritten.
    /// </summary>
}
