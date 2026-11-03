using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for fix7: a missing <c>secret-tool</c> on Linux raises one typed failure
/// that says what to do, on read and write alike. The key is still never assumed
/// absent, so the hook keeps failing closed rather than passing.
/// RED: <c>SecretServiceUnavailableException</c> does not exist yet.
/// </summary>
public sealed class ArbiterSecretToolMissingTests
{
    private const string TypeSafeOrigin = "https://api.typesafe.ai";

    [Fact]
    public void ReadWithoutSecretToolRaisesActionableFailure()
    {
        SecretServiceCredentialStore store = new(new MissingExecutableCredentialProcessRunner());

        SecretServiceUnavailableException exception = Assert.Throws<SecretServiceUnavailableException>(
            () => store.Read(TypeSafeOrigin));

        AssertActionable(exception.Message);
        Assert.IsType<Win32Exception>(exception.InnerException);
    }

    [Fact]
    public void WriteWithoutSecretToolRaisesTheSameActionableFailure()
    {
        SecretServiceCredentialStore store = new(new MissingExecutableCredentialProcessRunner());

        SecretServiceUnavailableException exception = Assert.Throws<SecretServiceUnavailableException>(
            () => store.Write(TypeSafeOrigin, "TS-WRITE-KEY-5e6f7a8b"));

        AssertActionable(exception.Message);
    }

    [Fact]
    public void MissingSecretToolFailureIsAnInvalidOperationForSetupAndStatus()
    {
        SecretServiceCredentialStore store = new(new MissingExecutableCredentialProcessRunner());

        InvalidOperationException exception = Assert.Throws<SecretServiceUnavailableException>(
            () => store.Read(TypeSafeOrigin));

        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public void ReadWithSecretToolPresentAndEntryAbsentStillReturnsNull()
    {
        FakeRunner present = new(new ProcessResult(1, string.Empty, "No such secret."));
        SecretServiceCredentialStore store = new(present);

        Assert.Null(store.Read(TypeSafeOrigin));
    }

    private static void AssertActionable(string message)
    {
        Assert.Contains("a remote provider needs a key", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TYPESAFE_API_KEY", message, StringComparison.Ordinal);
        Assert.Contains("libsecret-tools", message, StringComparison.Ordinal);
        Assert.Contains("kyber-weave arbiter setup", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain('\r', message);
    }

    /// <summary>
    /// Reproduces what <see cref="ProcessRunner"/> raises when <c>secret-tool</c> is not
    /// installed: <c>Process.Start</c> fails with ENOENT as a <see cref="Win32Exception"/>.
    /// </summary>
    internal sealed class MissingExecutableCredentialProcessRunner : ICredentialProcessRunner
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
        {
            ArgumentNullException.ThrowIfNull(startInfo);
            ArgumentNullException.ThrowIfNull(standardInput);
            throw new Win32Exception(
                2,
                $"An error occurred trying to start process '{startInfo.FileName}' with working directory " +
                "'/home/runner'. No such file or directory");
        }
    }

    private sealed class FakeRunner(ProcessResult next) : ICredentialProcessRunner
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
        {
            ArgumentNullException.ThrowIfNull(startInfo);
            ArgumentNullException.ThrowIfNull(standardInput);
            return next;
        }
    }
}
