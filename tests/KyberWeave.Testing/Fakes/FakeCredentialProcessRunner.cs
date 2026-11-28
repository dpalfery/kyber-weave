using System.Diagnostics;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests.Fakes;

/// <summary>One program call a <see cref="FakeCredentialProcessRunner"/> saw: the program, its argument list and what went to its standard input.</summary>
/// <param name="FileName">The program name.</param>
/// <param name="Argv">The argument list, in order.</param>
/// <param name="StandardInput">The text written to standard input.</param>
internal sealed record CapturedCredentialCall(string FileName, string[] Argv, string StandardInput);

/// <summary>The one fake for <see cref="ICredentialProcessRunner"/>: records each call and answers with a result the test sets, starting nothing.</summary>
internal sealed class FakeCredentialProcessRunner : ICredentialProcessRunner
{
    /// <summary>Gets the calls made, in order.</summary>
    public List<CapturedCredentialCall> Calls { get; } = [];

    /// <summary>Gets or sets the result every call answers with.</summary>
    public ProcessResult NextResult { get; set; } = new(0, string.Empty, string.Empty);

    /// <inheritdoc />
    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);
        string[] argv = [.. startInfo.ArgumentList];
        Calls.Add(new CapturedCredentialCall(startInfo.FileName, argv, standardInput));
        return NextResult;
    }

    /// <inheritdoc />
    public Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string standardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);
        cancellationToken.ThrowIfCancellationRequested();
        string[] argv = [.. startInfo.ArgumentList];
        Calls.Add(new CapturedCredentialCall(startInfo.FileName, argv, standardInput));
        return Task.FromResult(NextResult);
    }
}
