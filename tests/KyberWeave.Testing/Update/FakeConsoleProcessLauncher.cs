using KyberWeave.Cli.Update;

namespace KyberWeave.Testing.Update;

/// <summary>The console-sharing launcher in a unit test: it records each start and answers a scripted exit code or failure.</summary>
public sealed class FakeConsoleProcessLauncher : IConsoleProcessLauncher
{
    private readonly List<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, bool DiscardOutput)> _runs = [];

    /// <summary>Gets every start, in order.</summary>
    public IReadOnlyList<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, bool DiscardOutput)> Runs => _runs;

    /// <summary>Gets or sets the exit code a start answers.</summary>
    public int ExitCode { get; set; }

    /// <summary>Gets or sets the failure a start raises; null answers <see cref="ExitCode"/>.</summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    public int Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, bool discardOutput = false)
    {
        _runs.Add((fileName, arguments, timeout, discardOutput));
        return Failure is null ? ExitCode : throw Failure;
    }
}
