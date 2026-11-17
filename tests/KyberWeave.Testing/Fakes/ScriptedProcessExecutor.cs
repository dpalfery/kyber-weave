using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
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
public sealed class ScriptedProcessExecutor : IProcessExecutor
{
    private readonly Func<ProcessStartInfo, ProcessResult> _respond;
    private readonly List<ProcessStartInfo> _calls = [];

    public ScriptedProcessExecutor(Func<ProcessStartInfo, ProcessResult> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _respond = respond;
    }

    /// <summary>Every call the code under test made, in order.</summary>
    public IReadOnlyList<ProcessStartInfo> Calls => _calls;

    /// <summary>The calls whose executable is <paramref name="fileName"/>.</summary>
    public IEnumerable<ProcessStartInfo> CallsTo(string fileName) =>
        _calls.Where(call => call.FileName == fileName);

    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _calls.Add(startInfo);
        return _respond(startInfo);
    }
}
