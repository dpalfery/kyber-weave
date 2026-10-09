using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>macOS Keychain store for the Arbiter key.</summary>
/// <remarks>
/// Service <c>kyber-weave-arbiter</c>, account = endpoint origin. Writes go through
/// <c>security -i</c> with the <c>add-generic-password</c> command fed on stdin, so the
/// key travels on stdin only and never appears in argv. Reads use
/// <c>security find-generic-password -s kyber-weave-arbiter -a &lt;origin&gt; -w</c>.
/// A missing entry is an absent key (null), not a failure.
/// </remarks>
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    private const string Service = "kyber-weave-arbiter";

    private readonly ICredentialProcessRunner _runner;

    /// <summary>Creates a store that runs <c>security</c> through <paramref name="runner"/>.</summary>
    public MacKeychainCredentialStore(ICredentialProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <inheritdoc />
    public string? Read(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ProcessStartInfo startInfo = new("security")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("find-generic-password");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(Service);
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add(origin);
        startInfo.ArgumentList.Add("-w");

        ProcessResult result = _runner.Run(startInfo, string.Empty);
        if (result.ExitCode != 0)
        {
            return null;
        }

        string output = result.StandardOutput.TrimEnd('\r', '\n');
        return string.IsNullOrEmpty(output) ? null : output;
    }

    /// <inheritdoc />
    public void Write(string origin, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ProcessStartInfo startInfo = new("security")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-i");

        // Fed on stdin to `security -i`, not passed as argv: the key never appears in
        // the process argument list, where any local user could read it.
        string command = $"add-generic-password -U -s {Service} -a {origin} -w {key}\n";
        ProcessResult result = _runner.Run(startInfo, command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The macOS keychain write failed with exit code {result.ExitCode}.");
        }
    }
}
