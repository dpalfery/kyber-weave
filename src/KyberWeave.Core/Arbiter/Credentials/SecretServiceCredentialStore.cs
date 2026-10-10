using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>Linux Secret Service store for the Arbiter key.</summary>
/// <remarks>
/// Attributes <c>service=kyber-weave-arbiter</c> and <c>account=&lt;origin&gt;</c> under
/// the <c>Kyber Arbiter</c> label. The secret travels on stdin to
/// <c>secret-tool store</c> and never appears in argv. A missing entry is an absent key
/// (null), not a failure. A host whose <c>secret-tool</c> cannot be started raises
/// <see cref="SecretServiceUnavailableException"/> with the next step to take, from both
/// reads and writes.
/// </remarks>
public sealed class SecretServiceCredentialStore : ICredentialStore
{
    private const string Service = "kyber-weave-arbiter";

    private const string Label = "--label=Kyber Arbiter";

    private readonly ICredentialProcessRunner _runner;

    /// <summary>Creates a store that runs <c>secret-tool</c> through <paramref name="runner"/>.</summary>
    public SecretServiceCredentialStore(ICredentialProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <inheritdoc />
    public string? Read(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ProcessStartInfo startInfo = new("secret-tool")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("lookup");
        startInfo.ArgumentList.Add("service");
        startInfo.ArgumentList.Add(Service);
        startInfo.ArgumentList.Add("account");
        startInfo.ArgumentList.Add(origin);

        ProcessResult result = RunSecretTool(startInfo, string.Empty);
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
        ProcessStartInfo startInfo = new("secret-tool")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("store");
        startInfo.ArgumentList.Add(Label);
        startInfo.ArgumentList.Add("service");
        startInfo.ArgumentList.Add(Service);
        startInfo.ArgumentList.Add("account");
        startInfo.ArgumentList.Add(origin);

        // The secret travels on stdin only: argv carries the lookup attributes, where
        // any local user could read them, so the key must never be among them.
        ProcessResult result = RunSecretTool(startInfo, key);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The Secret Service write failed with exit code {result.ExitCode}.");
        }
    }

    private ProcessResult RunSecretTool(ProcessStartInfo startInfo, string standardInput)
    {
        try
        {
            return _runner.Run(startInfo, standardInput);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            // Process.Start raises Win32Exception when secret-tool is not on PATH. Mapping it
            // here keeps the raw OS text out of every surface and states the remedy instead.
            throw new SecretServiceUnavailableException(exception);
        }
    }
}
