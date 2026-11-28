using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
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
    public async Task ReadWithoutSecretToolRaisesActionableFailure()
    {
        SecretServiceCredentialStore store = new(new MissingExecutableCredentialProcessRunner());

        SecretServiceUnavailableException exception = await Assert.ThrowsAsync<SecretServiceUnavailableException>(
            () => store.ReadAsync(TypeSafeOrigin).AsTask());

        AssertActionable(exception.Message);
        Assert.IsType<Win32Exception>(exception.InnerException);
    }

    [Fact]
    public async Task WriteWithoutSecretToolRaisesTheSameActionableFailure()
    {
        SecretServiceCredentialStore store = new(new MissingExecutableCredentialProcessRunner());

        SecretServiceUnavailableException exception = await Assert.ThrowsAsync<SecretServiceUnavailableException>(
            () => store.WriteAsync(TypeSafeOrigin, "TS-WRITE-KEY-5e6f7a8b").AsTask());

        AssertActionable(exception.Message);
    }

    [Fact]
    public async Task MissingSecretToolFailureIsAnInvalidOperationForSetupAndStatus()
    {
        SecretServiceCredentialStore store = new(new MissingExecutableCredentialProcessRunner());

        InvalidOperationException exception = await Assert.ThrowsAsync<SecretServiceUnavailableException>(
            () => store.ReadAsync(TypeSafeOrigin).AsTask());

        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task ReadWithSecretToolPresentAndEntryAbsentStillReturnsNull()
    {
        FakeRunner present = new(new ProcessResult(1, string.Empty, "No such secret."));
        SecretServiceCredentialStore store = new(present);

        Assert.Null(await store.ReadAsync(TypeSafeOrigin));
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

    private sealed class FakeRunner(ProcessResult next) : ICredentialProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            string standardInput,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(startInfo);
            ArgumentNullException.ThrowIfNull(standardInput);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(next);
        }
    }
}
