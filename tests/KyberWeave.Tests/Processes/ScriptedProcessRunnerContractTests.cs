using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests.Processes;

/// <summary>Runs the process contract against the scripted fake, so unit tests can trust what it reports.</summary>
public sealed class ScriptedProcessRunnerContractTests : ProcessRunnerContractTests
{
    private const string WorkDirectory = "kw-process-probe-work";
    private const int LivePid = 4242;

    /// <inheritdoc />
    protected override IProcessRunner CreateRunner()
    {
        ScriptedProcessRunner runner = new();
        _ = runner
            .Script(ProcessProbeProgram.FakeProgram, Success("hello"), ProcessProbeProgram.Echo, "hello")
            .Script(ProcessProbeProgram.FakeProgram, new ProcessRunResult(0, "outerr", false) { StandardOutput = "out", StandardError = "err" }, ProcessProbeProgram.Both)
            .Script(ProcessProbeProgram.FakeProgram, new ProcessRunResult(7, "bad", false) { StandardOutput = string.Empty, StandardError = "bad" }, ProcessProbeProgram.Fail, "7", "bad")
            .Script(ProcessProbeProgram.FakeProgram, Success("piped text"), ProcessProbeProgram.Cat)
            .Script(ProcessProbeProgram.FakeProgram, Success("from-working-directory"), ProcessProbeProgram.Read, "marker.txt")
            .Script(ProcessProbeProgram.FakeProgram, Success(MarkerValue), ProcessProbeProgram.Environment, MarkerVariable)
            .ScriptTimeout(ProcessProbeProgram.FakeProgram, ProcessProbeProgram.Sleep, "10000")
            .WithMissingProgram(MissingProgram)
            .WithMissingDirectory(MissingDirectory)
            .WithProgramOnPath("dotnet", "fake-bin/dotnet")
            .WithAlive(LivePid);
        return runner;
    }

    /// <inheritdoc />
    protected override ProcessRunRequest Probe(
        string[] arguments,
        string? standardInput = null,
        TimeSpan? timeout = null,
        string? workingDirectory = null) =>
        new()
        {
            Program = ProcessProbeProgram.FakeProgram,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? WorkingDirectory,
            StandardInput = standardInput,
            Timeout = timeout
        };

    /// <inheritdoc />
    protected override string WorkingDirectory => WorkDirectory;

    /// <inheritdoc />
    protected override string MissingDirectory => "kw-process-probe-missing";

    /// <inheritdoc />
    protected override int LiveProcessId => LivePid;

    /// <inheritdoc />
    protected override void PrepareWorkingDirectoryFile(string name, string content)
    {
        // The fake answers from its script table; there is no directory and no file.
    }

    private static ProcessRunResult Success(string output) =>
        new(0, output, TimedOut: false) { StandardOutput = output, StandardError = string.Empty };
}
