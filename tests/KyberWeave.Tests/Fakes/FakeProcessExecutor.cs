using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Deterministic fake for <see cref="IProcessExecutor"/> that captures command calls and returns canned outputs.
/// </summary>
public sealed class FakeProcessExecutor : IProcessExecutor
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

    public FakeProcessExecutor WithFailure(string command, string message)
    {
        _cannedExceptions[command] = new InvalidOperationException(message);
        return this;
    }

    public ProcessResult Run(ProcessStartInfo startInfo, string input = "")
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
}
