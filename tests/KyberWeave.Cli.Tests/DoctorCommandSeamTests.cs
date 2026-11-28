using System.Diagnostics;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>doctor</c> resolves, prints and refuses, over the shared console, path and environment fakes and a
/// recording process runner: nothing here touches the disk, starts a process or reads the host's console.
/// </summary>
public sealed class DoctorCommandSeamTests : IDisposable
{
    private const string WorkingDirectory = "/kw/work";

    private readonly FakeConsole _console = new();
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeRepositoryDoctor _doctor = new();
    private readonly RecordingBoundedExecutor _processes = new();
    private readonly Dictionary<string, string?> _variables = new(StringComparer.Ordinal);
    private bool _terminal;

    public void Dispose()
    {
        _console.Dispose();
    }

    [Fact]
    public async Task Execute_MoreThanOneMode_RefusesOnTheErrorStreamAndReturnsTwo()
    {
        int exit = await Run(new DoctorSettings { Check = true, Ensure = true });

        Assert.Equal(2, exit);
        Assert.Equal(
            "kyber-weave: error: choose one of --check, --provision, --ensure or --post-checkout, not more than one." +
            Environment.NewLine,
            _console.ErrorOutput);
        Assert.Empty(_console.Output);
        Assert.Empty(_doctor.Runs);
    }

    [Fact]
    public async Task Execute_NonPositiveTimeout_RefusesOnTheErrorStreamAndReturnsTwo()
    {
        int exit = await Run(new DoctorSettings { IndexTimeoutSeconds = 0 });

        Assert.Equal(2, exit);
        Assert.Contains("must be positive seconds", _console.ErrorOutput, StringComparison.Ordinal);
        Assert.Empty(_doctor.Runs);
    }

    [Fact]
    public async Task Execute_RelativePath_IsResolvedAgainstTheWorkingDirectoryTheEnvironmentNames()
    {
        _ = await Run(new DoctorSettings { Path = "repo", Check = true });

        DoctorRun run = Assert.Single(_doctor.Runs);
        Assert.Equal("/kw/work/repo", run.Root);
    }

    [Fact]
    public async Task Execute_BareRunOnANonTerminal_ChecksAndPrintsTheHintBeforeTheReport()
    {
        _terminal = false;

        int exit = await Run(new DoctorSettings { Path = "." });

        Assert.Equal(0, exit);
        Assert.Equal(DoctorMode.Check, Assert.Single(_doctor.Runs).Mode);
        Assert.StartsWith(DoctorCommand.NonInteractiveHint + Environment.NewLine, _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_BareRunOnATerminalWithCiSet_ChecksInsteadOfProvisioning()
    {
        _terminal = true;
        _variables["CI"] = "true";

        _ = await Run(new DoctorSettings { Path = "." });

        Assert.Equal(DoctorMode.Check, Assert.Single(_doctor.Runs).Mode);
    }

    [Fact]
    public async Task Execute_BareRunOnATerminal_Provisions()
    {
        _terminal = true;

        _ = await Run(new DoctorSettings { Path = "." });

        Assert.Equal(DoctorMode.Provision, Assert.Single(_doctor.Runs).Mode);
        Assert.DoesNotContain(DoctorCommand.NonInteractiveHint, _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_JsonReport_IsWrittenToTheConsoleWithoutTheHint()
    {
        int exit = await Run(new DoctorSettings { Path = ".", Json = true });

        Assert.Equal(0, exit);
        Assert.StartsWith("{", _console.Output.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain(DoctorCommand.NonInteractiveHint, _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_NoExecutorGiven_RunsTheChildrenThroughTheProcessSeamBoundedByTheIndexTimeout()
    {
        _ = await Run(new DoctorSettings { Path = ".", Check = true, IndexTimeoutSeconds = 7 });

        DoctorRun run = Assert.Single(_doctor.Runs);
        // The doctor drives its children through RunAsync, so the bound is asserted there: a request that names
        // no timeout gets the index timeout.
        IProcessRunner runner = Assert.IsAssignableFrom<IProcessRunner>(run.Executor);
        _ = await runner.RunAsync(
            new ProcessRunRequest { Program = "codegraph", Arguments = [], StandardInput = "in" },
            CancellationToken.None);

        (ProcessStartInfo start, string input, TimeSpan timeout) = Assert.Single(_processes.Calls);
        Assert.Equal("codegraph", start.FileName);
        Assert.Equal("in", input);
        Assert.Equal(TimeSpan.FromSeconds(7), timeout);
    }

    [Fact]
    public async Task Execute_ReportExitCode_IsReturned()
    {
        FakeRepositoryDoctor doctor = new() { Report = new DoctorReport(".", DoctorMode.Check, 3, []) };

        int exit = await Run(new DoctorSettings { Path = ".", Check = true }, doctor);

        Assert.Equal(3, exit);
    }

    private async Task<int> Run(DoctorSettings settings, FakeRepositoryDoctor? doctor = null)
    {
        FakeProcessEnvironment environment = new(
            currentDirectory: WorkingDirectory,
            readVariable: name => _variables.GetValueOrDefault(name),
            isInputRedirected: !_terminal);
        DoctorCommand command = new(
            doctor ?? _doctor,
            environment,
            _console,
            new FakePathResolver(_files),
            _processes);
        return await command.ExecuteAsync(null!, settings, CancellationToken.None);
    }

    private sealed class RecordingBoundedExecutor : IProcessRunner
    {
        public List<(ProcessStartInfo Start, string Input, TimeSpan Timeout)> Calls { get; } = [];

        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput, TimeSpan timeout)
        {
            Calls.Add((startInfo, standardInput, timeout));
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            ProcessStartInfo startInfo = new(request.Program)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = request.WorkingDirectory ?? string.Empty
            };
            foreach (string argument in request.Arguments)
                startInfo.ArgumentList.Add(argument);

            Calls.Add((startInfo, request.StandardInput ?? string.Empty, request.Timeout ?? TimeSpan.Zero));
            return Task.FromResult(new ProcessRunResult(0, string.Empty, TimedOut: false));
        }

        public bool IsAlive(int processId) => false;

        public string? FindOnPath(string program) => null;
    }
}
