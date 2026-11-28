using System.Reflection;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;
using KyberWeave.Data.Arbiter.Credentials;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Credentials;

/// <summary>
/// Contract for task 1.8 (key resolution), the per-OS credential stores: the exact argument list each store hands its
/// program, the key on standard input only, the exit statuses that mean an absent key against a failed lookup, and the
/// gated Windows store. The program is a fake that records the call.
/// </summary>
public sealed class ArbiterCredentialStoreArgvTests
{
    private const string SentinelKey = "TS-SENTINEL-KEY-9f8e7d6c5b4a";

    private static readonly string[] MacWriteArgv = ["-i"];

    private static readonly string[] MacReadArgv =
        ["find-generic-password", "-s", "kyber-weave-arbiter", "-a", "https://api.typesafe.ai", "-w"];

    private static readonly string[] LinuxWriteArgv =
        ["store", "--label=Kyber Arbiter", "service", "kyber-weave-arbiter", "account", "https://api.typesafe.ai"];

    private static readonly string[] LinuxReadArgv =
        ["lookup", "service", "kyber-weave-arbiter", "account", "https://api.typesafe.ai"];

    [Fact]
    public async Task MacRead_TreatsTheItemNotFoundStatusAsAnAbsentKey()
    {
        // `security find-generic-password` exits 44 (errSecItemNotFound) when nothing
        // matches. Verified against the real tool: it exits 44, not 1.
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(44, string.Empty, "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain.");
        MacKeychainCredentialStore store = new(runner);

        Assert.Null(await store.ReadAsync("https://api.typesafe.ai"));
    }

    [Theory]
    // Every other non-zero exit is a failure of the lookup itself -- a locked keychain, a
    // denied ACL, a broken security binary. Reading those as "no key" tells the user to
    // store one and silently drops the key on the floor.
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(36)]
    [InlineData(127)]
    public async Task MacRead_RaisesRatherThanReportingAnAbsentKey(int exitCode)
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(exitCode, string.Empty, "security: some other failure");
        MacKeychainCredentialStore store = new(runner);

        InvalidOperationException error = await Assert.ThrowsAsync<KeychainUnavailableException>(
            () => store.ReadAsync("https://api.typesafe.ai").AsTask());
        Assert.Contains(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacWriteKeepsKeyOnStdinOnly()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, string.Empty, string.Empty);
        MacKeychainCredentialStore store = new(runner);

        await store.WriteAsync("https://api.typesafe.ai", SentinelKey);

        CapturedCredentialCall call = Assert.Single(runner.Calls);
        Assert.Equal("security", call.FileName);
        Assert.Equal(MacWriteArgv, call.Argv);
        Assert.Contains("add-generic-password", call.StandardInput, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", call.StandardInput, StringComparison.Ordinal);
        Assert.Contains("https://api.typesafe.ai", call.StandardInput, StringComparison.Ordinal);
        Assert.Contains(SentinelKey, call.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelKey, string.Join(" ", call.Argv), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacWriteRejectsANonAsciiKeyBeforeRunningSecurity()
    {
        FakeCredentialProcessRunner runner = new();
        MacKeychainCredentialStore store = new(runner);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync("https://api.typesafe.ai", "sk-ünïcø∂é").AsTask());

        Assert.Empty(runner.Calls);
        Assert.Contains("ASCII", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacReadRejectsAnOriginWithALineBreakBeforeRunningSecurity()
    {
        FakeCredentialProcessRunner runner = new();
        MacKeychainCredentialStore store = new(runner);

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync("https://kyber-weave\ncontract.invalid").AsTask());

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task LinuxRejectsALineBreakInTheOriginOrKeyBeforeRunningSecretTool()
    {
        FakeCredentialProcessRunner runner = new();
        SecretServiceCredentialStore store = new(runner);

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync("https://kyber-weave\ncontract.invalid").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync("https://kyber-weave\rcontract.invalid", "sk-contract").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync("https://kyber-weave\ncontract.invalid").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync("https://api.typesafe.ai", "line-one\nline-two").AsTask());

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task MacReadUsesExactArgvAndReturnsTrimmedStdout()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, "stored-key\n", string.Empty);
        MacKeychainCredentialStore store = new(runner);

        string? key = await store.ReadAsync("https://api.typesafe.ai");

        Assert.Equal("stored-key", key);
        CapturedCredentialCall call = Assert.Single(runner.Calls);
        Assert.Equal("security", call.FileName);
        Assert.Equal(MacReadArgv, call.Argv);
    }

    [Fact]
    public async Task MacReadReturnsNullWhenEntryMissing()
    {
        FakeCredentialProcessRunner failing = new();
        failing.NextResult = new ProcessResult(44, string.Empty, "not found");
        MacKeychainCredentialStore missing = new(failing);

        Assert.Null(await missing.ReadAsync("https://api.typesafe.ai"));

        FakeCredentialProcessRunner empty = new();
        empty.NextResult = new ProcessResult(0, "\n", string.Empty);
        MacKeychainCredentialStore blank = new(empty);

        Assert.Null(await blank.ReadAsync("https://api.typesafe.ai"));
    }

    [Fact]
    public async Task LinuxWriteKeepsKeyOnStdinOnly()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, string.Empty, string.Empty);
        SecretServiceCredentialStore store = new(runner);

        await store.WriteAsync("https://api.typesafe.ai", SentinelKey);

        CapturedCredentialCall call = Assert.Single(runner.Calls);
        Assert.Equal("secret-tool", call.FileName);
        Assert.Equal(LinuxWriteArgv, call.Argv);
        Assert.Equal(SentinelKey, call.StandardInput);
        Assert.DoesNotContain(SentinelKey, string.Join(" ", call.Argv), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxReadUsesExactArgvAndReturnsTrimmedStdout()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, "stored-key\n", string.Empty);
        SecretServiceCredentialStore store = new(runner);

        string? key = await store.ReadAsync("https://api.typesafe.ai");

        Assert.Equal("stored-key", key);
        CapturedCredentialCall call = Assert.Single(runner.Calls);
        Assert.Equal("secret-tool", call.FileName);
        Assert.Equal(LinuxReadArgv, call.Argv);
    }

    [Fact]
    public async Task LinuxReadReturnsNullWhenEntryMissing()
    {
        FakeCredentialProcessRunner failing = new();
        failing.NextResult = new ProcessResult(1, string.Empty, "no match");
        SecretServiceCredentialStore missing = new(failing);

        Assert.Null(await missing.ReadAsync("https://api.typesafe.ai"));
    }

    [Fact]
    public async Task LinuxReadWithEmptyStderrIsAFailureNotAnAbsentKey()
    {
        FakeCredentialProcessRunner failing = new();
        failing.NextResult = new ProcessResult(1, string.Empty, string.Empty);
        SecretServiceCredentialStore store = new(failing);

        _ = await Assert.ThrowsAsync<SecretServiceUnavailableException>(
            () => store.ReadAsync("https://api.typesafe.ai").AsTask());
    }

    [Fact]
    public async Task LinuxReadSurfacesALookupFailureInsteadOfReportingAnAbsentKey()
    {
        FakeCredentialProcessRunner failing = new();
        failing.NextResult = new ProcessResult(
            1,
            string.Empty,
            "Cannot autolaunch D-Bus without X11 $DISPLAY");
        SecretServiceCredentialStore store = new(failing);

        SecretServiceUnavailableException ex = await Assert.ThrowsAsync<SecretServiceUnavailableException>(
            () => store.ReadAsync("https://api.typesafe.ai").AsTask());

        Assert.Contains("exit code 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D-Bus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsStoreDeclaresAdvapi32EntryPointsAndIsGatedByOperatingSystem()
    {
        Type? native = typeof(WindowsCredentialStore).GetNestedType("NativeMethods", BindingFlags.NonPublic);
        Assert.NotNull(native);
        string[] names = native!
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();
        Assert.Contains("CredWriteW", names);
        Assert.Contains("CredReadW", names);
        Assert.Contains("CredFree", names);

        WindowsCredentialStore store = new();
        if (OperatingSystem.IsWindows())
        {
            string? missing = await store.ReadAsync("https://missing-origin.invalid");
            Assert.Null(missing);
        }
        else
        {
            PlatformNotSupportedException readFailure = await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => store.ReadAsync("https://api.typesafe.ai").AsTask());
            PlatformNotSupportedException writeFailure = await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => store.WriteAsync("https://api.typesafe.ai", SentinelKey).AsTask());
            Assert.DoesNotContain(SentinelKey, readFailure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelKey, writeFailure.Message, StringComparison.Ordinal);
        }
    }
}
