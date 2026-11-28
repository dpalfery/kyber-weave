using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using KyberWeave.Data.Arbiter.Credentials;
using Xunit;

namespace KyberWeave.Tests.Credentials;

/// <summary>
/// Contract for task fix1, F2: the macOS keychain store takes a key with a space, a quote or a backslash on the
/// <c>security -i</c> standard input, quoted, never on the argument list, and refuses a key with a line break.
/// </summary>
public sealed class MacKeychainWriteTests
{
    // F2: keys with space, quote and backslash must round-trip through the
    // `security -i` stdin command, never on argv; newline must be rejected.
    [Theory]
    [InlineData("key with space")]
    [InlineData("key\"with\"quotes")]
    [InlineData("key\\with\\backslash")]
    public async Task Fix2_MacWrite_SpecialKeysStayQuotedOnStdinOnly(string key)
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, string.Empty, string.Empty);
        MacKeychainCredentialStore store = new(runner);

        await store.WriteAsync("https://api.typesafe.ai", key);

        CapturedCredentialCall call = Assert.Single(runner.Calls);
        Assert.Equal(["-i"], call.Argv);
        Assert.DoesNotContain(key, string.Join(" ", call.Argv), StringComparison.Ordinal);
        // The key must be quoted so `security -i` parses it as one argument.
        Assert.Contains($"\"{key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"", call.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix2_MacWrite_NewlineKey_RejectedWithClearError()
    {
        FakeCredentialProcessRunner runner = new();
        MacKeychainCredentialStore store = new(runner);

        ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync("https://api.typesafe.ai", "abc\ndef").AsTask());

        Assert.DoesNotContain("abc\ndef", failure.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }
}
