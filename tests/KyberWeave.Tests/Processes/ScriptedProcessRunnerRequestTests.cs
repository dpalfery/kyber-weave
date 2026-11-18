using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Processes;

/// <summary>
/// The scripted fake must answer from the request the seam received, not from fixed text alone. Otherwise the
/// shared contract would stay green while the seam dropped the working directory, environment, input or timeout.
/// </summary>
public sealed class ScriptedProcessRunnerRequestTests
{
    [Fact]
    public async Task AnswersFromStandardInput()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.Script("probe", request => Result(request.StandardInput ?? string.Empty), "cat");

        ProcessRunResult first = await Run(runner, new ProcessRunRequest { Program = "probe", Arguments = ["cat"], StandardInput = "first" });
        ProcessRunResult second = await Run(runner, new ProcessRunRequest { Program = "probe", Arguments = ["cat"], StandardInput = "second" });

        Assert.Equal("first", first.StandardOutput);
        Assert.Equal("second", second.StandardOutput);
    }

    [Fact]
    public async Task AnswersFromTheEnvironment()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.Script(
            "probe",
            request => Result(request.Environment?.GetValueOrDefault("KW_TEST_MARKER") ?? "<unset>"),
            "env");

        ProcessRunRequest request = new()
        {
            Program = "probe",
            Arguments = ["env"],
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["KW_TEST_MARKER"] = "value" }
        };

        Assert.Equal("value", (await Run(runner, request)).StandardOutput);
    }

    [Fact]
    public async Task AnswersFromTheWorkingDirectory()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.Script("probe", request => Result(request.WorkingDirectory ?? "<none>"), "cwd");

        ProcessRunResult first = await Run(runner, new ProcessRunRequest { Program = "probe", Arguments = ["cwd"], WorkingDirectory = "first" });
        ProcessRunResult second = await Run(runner, new ProcessRunRequest { Program = "probe", Arguments = ["cwd"], WorkingDirectory = "second" });

        Assert.Equal("first", first.StandardOutput);
        Assert.Equal("second", second.StandardOutput);
    }

    [Fact]
    public async Task AnswersFromTheTimeout()
    {
        ScriptedProcessRunner runner = new();
        _ = runner.Script(
            "probe",
            request => request.Timeout is null ? Result("no timeout") : new ProcessRunResult(-1, string.Empty, TimedOut: true),
            "slow");

        ProcessRunResult unbounded = await Run(runner, new ProcessRunRequest { Program = "probe", Arguments = ["slow"] });
        ProcessRunResult bounded = await Run(runner, new ProcessRunRequest { Program = "probe", Arguments = ["slow"], Timeout = TimeSpan.FromSeconds(1) });

        Assert.False(unbounded.TimedOut);
        Assert.True(bounded.TimedOut);
    }

    private static Task<ProcessRunResult> Run(ScriptedProcessRunner runner, ProcessRunRequest request) =>
        runner.RunAsync(request, CancellationToken.None);

    private static ProcessRunResult Result(string output) =>
        new(0, output, TimedOut: false) { StandardOutput = output, StandardError = string.Empty };
}
