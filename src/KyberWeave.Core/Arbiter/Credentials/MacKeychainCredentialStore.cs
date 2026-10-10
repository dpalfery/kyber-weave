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

    /// <summary>
    /// <c>security find-generic-password</c> exits 44 (errSecItemNotFound) when no entry
    /// matches. Verified against the real tool rather than assumed.
    /// </summary>
    private const int ItemNotFoundExitCode = 44;

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
        if (result.ExitCode == ItemNotFoundExitCode)
        {
            // errSecItemNotFound: nothing matched these attributes, so there is no key.
            // This is the only non-zero exit that means "absent"; every other one is a
            // failed lookup and must not be reported to the operator as a missing key.
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw new KeychainUnavailableException(
                $"The macOS keychain read failed with exit code {result.ExitCode}: " +
                $"{OneLine(result.StandardError)}");
        }

        string output = result.StandardOutput.TrimEnd('\r', '\n');
        return string.IsNullOrEmpty(output) ? null : output;
    }

    /// <inheritdoc />
    public void Write(string origin, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (HasNewline(origin))
        {
            throw new ArgumentException(
                "The macOS keychain origin cannot contain newline characters.", nameof(origin));
        }

        if (HasNewline(key))
        {
            throw new ArgumentException(
                "The macOS keychain key cannot contain newline characters: a newline would split the 'security -i' command.", nameof(key));
        }

        ProcessStartInfo startInfo = new("security")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-i");

        // Fed on stdin to `security -i`, not passed as argv: the key never appears in
        // the process argument list, where any local user could read it. Origin and key
        // are double-quoted with backslash and quote escaping so a space, quote or
        // backslash is parsed as one argument; a newline cannot be quoted on this
        // line protocol and is rejected above.
        string command = $"add-generic-password -U -s {Service} -a {Quote(origin)} -w {Quote(key)}\n";
        ProcessResult result = _runner.Run(startInfo, command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The macOS keychain write failed with exit code {result.ExitCode}.");
        }
    }

    private static bool HasNewline(string value) =>
        value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal);

    private static string OneLine(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
