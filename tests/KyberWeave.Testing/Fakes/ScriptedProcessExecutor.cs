using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Scripted <see cref="IProcessExecutor"/> for doctor tests: each call is answered by a delegate that sees the
/// executable and its argument list, and every call is recorded. Nothing is spawned.
/// </summary>
/// <remarks>
/// Use this when the answer depends on the verb (<c>init</c> versus <c>index</c>) or the working directory.
/// <see cref="FakeProcessExecutor"/> keys only on the executable name and cannot tell those apart.
/// Tests read <see cref="ProcessStartInfo.ArgumentList"/> and <see cref="ProcessStartInfo.WorkingDirectory"/>
/// from the recorded calls rather than constructing a <see cref="ProcessStartInfo"/> themselves.
/// </remarks>
public sealed class ScriptedProcessExecutor : IProcessRunner
{
    private readonly Func<ProcessStartInfo, ProcessResult> _respond;
    private readonly List<ProcessStartInfo> _calls = [];
    private readonly List<TimeSpan> _timeouts = [];
    private readonly List<string> _inputs = [];

    public ScriptedProcessExecutor(Func<ProcessStartInfo, ProcessResult> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _respond = respond;
    }

    /// <summary>Every call the code under test made, in order.</summary>
    public IReadOnlyList<ProcessStartInfo> Calls => _calls;

    /// <summary>The time limit each bounded call passed, in order; an unbounded call adds none.</summary>
    public IReadOnlyList<TimeSpan> Timeouts => _timeouts;

    /// <summary>The standard input each call was given, in order.</summary>
    public IReadOnlyList<string> Inputs => _inputs;

    /// <summary>The calls whose executable is <paramref name="fileName"/>.</summary>
    public IEnumerable<ProcessStartInfo> CallsTo(string fileName) =>
        _calls.Where(call => call.FileName == fileName);

    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _calls.Add(startInfo);
        _inputs.Add(standardInput);
        return _respond(startInfo);
    }

    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput, TimeSpan timeout)
    {
        _timeouts.Add(timeout);
        return Run(startInfo, standardInput);
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

        _calls.Add(startInfo);
        _inputs.Add(request.StandardInput ?? string.Empty);
        if (request.Timeout is { } timeout)
            _timeouts.Add(timeout);

        ProcessResult result = _respond(startInfo);
        return Task.FromResult(new ProcessRunResult(result.ExitCode, result.StandardOutput, TimedOut: false)
        {
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError
        });
    }

    public bool IsAlive(int processId) => false;

    public string? FindOnPath(string program) => null;
}
