using System.Diagnostics;
using KyberWeave.Cli;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Deterministic fake for <see cref="IProcessRunner"/> and <see cref="IProcessRunner"/> that captures command calls and returns canned outputs.
/// </summary>
public sealed class FakeProcessExecutor : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult> _cannedOutputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _cannedExceptions = new(StringComparer.Ordinal);
    private readonly List<ProcessStartInfo> _calls = [];

    public IReadOnlyList<ProcessStartInfo> Calls => _calls;

    public FakeProcessExecutor WithProbeOutput(string command, string stdout, int exitCode = 0)
    {
        _cannedOutputs[command] = new ProcessResult(exitCode, stdout, string.Empty);
        return this;
    }

    /// <summary>
    /// Stubs <c>kyber-weave-arbiter --version</c> with the CLI version doctor accepts
    /// when the repository under test has enabled the Arbiter.
    /// </summary>
    public FakeProcessExecutor WithMatchingArbiterProbe()
    {
        string version = CliAssemblyVersion.ForProbe(typeof(SquadDoctorCommand).Assembly);
        return WithProbeOutput("kyber-weave-arbiter", $"kyber-weave-arbiter {version}\n");
    }

    public FakeProcessExecutor WithFailure(string command, string message)
    {
        _cannedExceptions[command] = new InvalidOperationException(message);
        return this;
    }

    /// <summary>Makes the command throw the given exception, as a process start can when the file is not executable.</summary>
    public FakeProcessExecutor WithException(string command, Exception exception)
    {
        _cannedExceptions[command] = exception;
        return this;
    }

    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput = "")
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _calls.Add(startInfo);

        string executable = Path.GetFileName(startInfo.FileName);
        if (_cannedExceptions.TryGetValue(executable, out Exception? ex))
            throw ex;

        if (_cannedOutputs.TryGetValue(executable, out ProcessResult result))
            return result;

        return new ProcessResult(0, string.Empty, string.Empty);
    }

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new(request.Program)
        {
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        if (request.Environment is { } environment)
        {
            startInfo.Environment.Clear();
            foreach (KeyValuePair<string, string?> pair in environment)
            {
                if (pair.Value is not null)
                    startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        ProcessResult result = Run(startInfo, request.StandardInput ?? string.Empty);
        return Task.FromResult(new ProcessRunResult(result.ExitCode, result.StandardOutput + result.StandardError, TimedOut: false)
        {
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError
        });
    }

    public bool IsAlive(int processId) => false;

    public string? FindOnPath(string program) => null;
}
