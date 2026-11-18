using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Processes;

/// <summary>
/// The three role interfaces are views over the one seam. A caller that still holds an old role must reach the
/// same implementation and keep the exception and stream behaviour it had before the seam existed.
/// </summary>
public sealed class ProcessRunnerRoleViewTests
{
    [Fact]
    public void TheProgramRunnerViewKeepsTheStreamsApart()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.Script("probe", new ProcessRunResult(0, "outerr", false) { StandardOutput = "out", StandardError = "err" }, "hello");

        ProcessResult result = ((IProgramRunner)runner).Run("probe", ["hello"], "work");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
        ProcessRunRequest call = Assert.Single(runner.Calls);
        Assert.Equal("probe", call.Program);
        Assert.Equal(["hello"], call.Arguments);
        Assert.Equal("work", call.WorkingDirectory);
    }

    [Fact]
    public void TheProgramRunnerViewReportsATimeoutAsTimeoutException()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.ScriptTimeout("probe", "slow");

        Assert.Throws<TimeoutException>(() =>
            ((IProgramRunner)runner).Run("probe", ["slow"], "work", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void TheProgramRunnerViewReportsAStartFailureAsWin32Exception()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.WithMissingProgram("probe");

        Assert.Throws<Win32Exception>(() =>
            ((IProgramRunner)runner).Run("probe", [], "work"));
    }

    [Fact]
    public void TheExecutorViewTranslatesTheStartInfoAndWritesItsInput()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.Script("probe", new ProcessRunResult(3, "bad", false) { StandardOutput = string.Empty, StandardError = "bad" }, "hello");
        ProcessStartInfo startInfo = new("probe") { WorkingDirectory = "work" };
        startInfo.ArgumentList.Add("hello");

        ProcessResult result = ((IProcessExecutor)runner).Run(startInfo, "input text");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("bad", result.StandardError);
        ProcessRunRequest call = Assert.Single(runner.Calls);
        Assert.Equal("probe", call.Program);
        Assert.Equal(["hello"], call.Arguments);
        Assert.Equal("work", call.WorkingDirectory);
        Assert.Equal("input text", call.StandardInput);
    }

    [Fact]
    public void TheBoundedExecutorViewPassesItsTimeoutToTheSeam()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.ScriptTimeout("probe");
        ProcessStartInfo startInfo = new("probe");

        Assert.Throws<TimeoutException>(() =>
            ((IBoundedProcessExecutor)runner).Run(startInfo, string.Empty, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(runner.Calls).Timeout);
    }
}
