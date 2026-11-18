using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests.Processes;

/// <summary>Runs the process contract against the scripted fake, so unit tests can trust what it reports.</summary>
public sealed class ScriptedProcessRunnerContractTests : ProcessRunnerContractTests
{
    private const string WorkDirectory = "kw-process-probe-work";
    private const int LivePid = 4242;

    private readonly Dictionary<(string? Directory, string Name), string> _files = [];

    /// <inheritdoc />
    protected override IProcessRunner CreateRunner()
    {
        ScriptedProcessRunner runner = new();
        _ = runner
            .Script(ProcessProbeProgram.FakeProgram, Success("hello"), ProcessProbeProgram.Echo, "hello")
            .Script(ProcessProbeProgram.FakeProgram, Success(new string('x', 200)), ProcessProbeProgram.Echo, new string('x', 200))
            .Script(ProcessProbeProgram.FakeProgram, new ProcessRunResult(0, "outerr", false) { StandardOutput = "out", StandardError = "err" }, ProcessProbeProgram.Both)
            .Script(ProcessProbeProgram.FakeProgram, new ProcessRunResult(7, "bad", false) { StandardOutput = string.Empty, StandardError = "bad" }, ProcessProbeProgram.Fail, "7", "bad")
            .Script(ProcessProbeProgram.FakeProgram, request => Success(request.StandardInput ?? string.Empty), ProcessProbeProgram.Cat)
            .Script(ProcessProbeProgram.FakeProgram, request => Success(ReadPrepared(request, "marker.txt")), ProcessProbeProgram.Read, "marker.txt")
            .Script(ProcessProbeProgram.FakeProgram, request => Success(EnvironmentValue(request, MarkerVariable)), ProcessProbeProgram.Environment, MarkerVariable)
            .Script(ProcessProbeProgram.FakeProgram, TimedOutWhenRequested, ProcessProbeProgram.Sleep, "10000")
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
        string? workingDirectory = null,
        long? maxOutputBytes = null) =>
        new()
        {
            Program = ProcessProbeProgram.FakeProgram,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? WorkingDirectory,
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal) { [MarkerVariable] = MarkerValue },
            StandardInput = standardInput,
            Timeout = timeout,
            MaxOutputBytes = maxOutputBytes
        };

    /// <inheritdoc />
    protected override string WorkingDirectory => WorkDirectory;

    /// <inheritdoc />
    protected override string MissingDirectory => "kw-process-probe-missing";

    /// <inheritdoc />
    protected override int LiveProcessId => LivePid;

    /// <inheritdoc />
    protected override void PrepareWorkingDirectoryFile(string name, string content) =>
        _files[(WorkingDirectory, name)] = content;

    private string ReadPrepared(ProcessRunRequest request, string name) =>
        _files.TryGetValue((request.WorkingDirectory, name), out string? content) ? content : "<missing>";

    private static string EnvironmentValue(ProcessRunRequest request, string name) =>
        request.Environment is { } environment && environment.TryGetValue(name, out string? value)
            ? value ?? "<unset>"
            : "<unset>";

    private static ProcessRunResult TimedOutWhenRequested(ProcessRunRequest request) =>
        request.Timeout is not null
            ? new ProcessRunResult(-1, string.Empty, TimedOut: true) { StandardOutput = string.Empty, StandardError = string.Empty }
            : Success(string.Empty);

    private static ProcessRunResult Success(string output) =>
        new(0, output, TimedOut: false) { StandardOutput = output, StandardError = string.Empty };
}
