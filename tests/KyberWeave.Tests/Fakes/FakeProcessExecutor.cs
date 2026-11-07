using System.Diagnostics;
using System.Reflection;
using KyberWeave.Cli.Commands.Squad;
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

    /// <summary>
    /// Stubs <c>kyber-weave-arbiter --version</c> with the CLI version doctor accepts
    /// when the repository under test has enabled the Arbiter.
    /// </summary>
    public FakeProcessExecutor WithMatchingArbiterProbe()
    {
        Assembly assembly = typeof(SquadDoctorCommand).Assembly;
        string? infoVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = string.IsNullOrWhiteSpace(infoVersion)
            ? assembly.GetName().Version?.ToString() ?? "0.0.0"
            : infoVersion;
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            version = version[..plus];
        }

        version = version.Trim().TrimStart('v');
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
}
