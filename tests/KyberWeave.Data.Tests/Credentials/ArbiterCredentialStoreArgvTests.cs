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
    public void MacRead_TreatsTheItemNotFoundStatusAsAnAbsentKey()
    {
        // `security find-generic-password` exits 44 (errSecItemNotFound) when nothing
        // matches. Verified against the real tool: it exits 44, not 1.
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(44, string.Empty, "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain.");
        MacKeychainCredentialStore store = new(runner);

        Assert.Null(store.Read("https://api.typesafe.ai"));
    }

    [Theory]
    // Every other non-zero exit is a failure of the lookup itself -- a locked keychain, a
    // denied ACL, a broken security binary. Reading those as "no key" tells the user to
    // store one and silently drops the key on the floor.
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(36)]
    [InlineData(127)]
    public void MacRead_RaisesRatherThanReportingAnAbsentKey(int exitCode)
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(exitCode, string.Empty, "security: some other failure");
        MacKeychainCredentialStore store = new(runner);

        InvalidOperationException error = Assert.Throws<KeychainUnavailableException>(
            () => store.Read("https://api.typesafe.ai"));
        Assert.Contains(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MacWriteKeepsKeyOnStdinOnly()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, string.Empty, string.Empty);
        MacKeychainCredentialStore store = new(runner);

        store.Write("https://api.typesafe.ai", SentinelKey);

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
    public void MacWriteRejectsANonAsciiKeyBeforeRunningSecurity()
    {
        FakeCredentialProcessRunner runner = new();
        MacKeychainCredentialStore store = new(runner);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => store.Write("https://api.typesafe.ai", "sk-ünïcø∂é"));

        Assert.Empty(runner.Calls);
        Assert.Contains("ASCII", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MacReadRejectsAnOriginWithALineBreakBeforeRunningSecurity()
    {
        FakeCredentialProcessRunner runner = new();
        MacKeychainCredentialStore store = new(runner);

        _ = Assert.ThrowsAny<ArgumentException>(() => store.Read("https://kyber-weave\ncontract.invalid"));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void LinuxRejectsALineBreakInTheOriginOrKeyBeforeRunningSecretTool()
    {
        FakeCredentialProcessRunner runner = new();
        SecretServiceCredentialStore store = new(runner);

        _ = Assert.ThrowsAny<ArgumentException>(() => store.Read("https://kyber-weave\ncontract.invalid"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write("https://kyber-weave\rcontract.invalid", "sk-contract"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Delete("https://kyber-weave\ncontract.invalid"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write("https://api.typesafe.ai", "line-one\nline-two"));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void MacReadUsesExactArgvAndReturnsTrimmedStdout()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, "stored-key\n", string.Empty);
        MacKeychainCredentialStore store = new(runner);

        string? key = store.Read("https://api.typesafe.ai");

        Assert.Equal("stored-key", key);
        CapturedCredentialCall call = Assert.Single(runner.Calls);
        Assert.Equal("security", call.FileName);
        Assert.Equal(MacReadArgv, call.Argv);
    }

    [Fact]
    public void MacReadReturnsNullWhenEntryMissing()
    {
        FakeCredentialProcessRunner failing = new();
        failing.NextResult = new ProcessResult(44, string.Empty, "not found");
        MacKeychainCredentialStore missing = new(failing);

        Assert.Null(missing.Read("https://api.typesafe.ai"));

        FakeCredentialProcessRunner empty = new();
        empty.NextResult = new ProcessResult(0, "\n", string.Empty);
        MacKeychainCredentialStore blank = new(empty);

        Assert.Null(blank.Read("https://api.typesafe.ai"));
    }

    [Fact]
    public void LinuxWriteKeepsKeyOnStdinOnly()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, string.Empty, string.Empty);
        SecretServiceCredentialStore store = new(runner);

        store.Write("https://api.typesafe.ai", SentinelKey);

        CapturedCredentialCall call = Assert.Single(runner.Calls);
        Assert.Equal("secret-tool", call.FileName);
        Assert.Equal(LinuxWriteArgv, call.Argv);
        Assert.Equal(SentinelKey, call.StandardInput);
        Assert.DoesNotContain(SentinelKey, string.Join(" ", call.Argv), StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxReadUsesExactArgvAndReturnsTrimmedStdout()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, "stored-key\n", string.Empty);
        SecretServiceCredentialStore store = new(runner);

        string? key = store.Read("https://api.typesafe.ai");

        Assert.Equal("stored-key", key);
        CapturedCredentialCall call = Assert.Single(runner.Calls);
        Assert.Equal("secret-tool", call.FileName);
        Assert.Equal(LinuxReadArgv, call.Argv);
    }

    [Fact]
    public void LinuxReadReturnsNullWhenEntryMissing()
    {
        FakeCredentialProcessRunner failing = new();
        failing.NextResult = new ProcessResult(1, string.Empty, "no match");
        SecretServiceCredentialStore missing = new(failing);

        Assert.Null(missing.Read("https://api.typesafe.ai"));
    }

    [Fact]
    public void LinuxReadWithEmptyStderrIsAFailureNotAnAbsentKey()
    {
        FakeCredentialProcessRunner failing = new();
        failing.NextResult = new ProcessResult(1, string.Empty, string.Empty);
        SecretServiceCredentialStore store = new(failing);

        Assert.Throws<SecretServiceUnavailableException>(
            () => store.Read("https://api.typesafe.ai"));
    }

    [Fact]
    public void LinuxReadSurfacesALookupFailureInsteadOfReportingAnAbsentKey()
    {
        FakeCredentialProcessRunner failing = new();
        failing.NextResult = new ProcessResult(
            1,
            string.Empty,
            "Cannot autolaunch D-Bus without X11 $DISPLAY");
        SecretServiceCredentialStore store = new(failing);

        SecretServiceUnavailableException ex =
            Assert.Throws<SecretServiceUnavailableException>(() => store.Read("https://api.typesafe.ai"));

        Assert.Contains("exit code 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D-Bus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsStoreDeclaresAdvapi32EntryPointsAndIsGatedByOperatingSystem()
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
            string? missing = store.Read("https://missing-origin.invalid");
            Assert.Null(missing);
        }
        else
        {
            PlatformNotSupportedException readFailure =
                Assert.Throws<PlatformNotSupportedException>(() => store.Read("https://api.typesafe.ai"));
            PlatformNotSupportedException writeFailure =
                Assert.Throws<PlatformNotSupportedException>(() => store.Write("https://api.typesafe.ai", SentinelKey));
            Assert.DoesNotContain(SentinelKey, readFailure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelKey, writeFailure.Message, StringComparison.Ordinal);
        }
    }
}
