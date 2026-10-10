using System.Diagnostics;
using System.Reflection;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.8 (key resolution): the <c>TYPESAFE_API_KEY</c> origin binding,
/// the per-OS credential stores with the key on stdin only, the gated Windows store,
/// the per-user provider override and its diagnostics, and the no-leak guarantee.
/// RED: <c>ArbiterKeyResolver</c> and the stores do not exist yet.
/// </summary>
public sealed class ArbiterKeyResolutionTests
{
    private const string SentinelKey = "TS-SENTINEL-KEY-9f8e7d6c5b4a";

    private static readonly string[] ForeignOrigin = ["https://other.example.com"];

    private static readonly string[] TypeSafeOrigin = ["https://api.typesafe.ai"];

    private static readonly string[] MacWriteArgv = ["-i"];

    private static readonly string[] MacReadArgv =
        ["find-generic-password", "-s", "kyber-weave-arbiter", "-a", "https://api.typesafe.ai", "-w"];

    private static readonly string[] LinuxWriteArgv =
        ["store", "--label=Kyber Arbiter", "service", "kyber-weave-arbiter", "account", "https://api.typesafe.ai"];

    private static readonly string[] LinuxReadArgv =
        ["lookup", "service", "kyber-weave-arbiter", "account", "https://api.typesafe.ai"];

    private static Func<string, string?> EnvReader(string? value) =>
        (string name) => string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? value : null;

    [Fact]
    public void EnvKeyServesTypeSafeOriginWithoutTouchingStore()
    {
        FakeCredentialStore store = new() { NextKey = "store-key" };

        string? resolved = ArbiterKeyResolver.Resolve(
            "https://api.typesafe.ai/v1",
            store,
            EnvReader(SentinelKey));

        Assert.Equal(SentinelKey, resolved);
        Assert.Empty(store.RequestedOrigins);
    }

    [Fact]
    public void EnvKeyIgnoredForForeignOrigin()
    {
        FakeCredentialStore store = new() { NextKey = "store-key" };

        string? resolved = ArbiterKeyResolver.Resolve(
            "https://other.example.com/v1",
            store,
            EnvReader(SentinelKey));

        Assert.Equal("store-key", resolved);
        Assert.Equal(ForeignOrigin, store.RequestedOrigins);
    }

    [Theory]
    [InlineData("https://proxy.example.com/v1", "https://proxy.example.com/v1", SentinelKey)]
    [InlineData("https://proxy.example.com/v1", "https://elsewhere.example.com/v1", "store-key")]
    public void EnvKeyHonoredOnlyForUserOverrideOrigin(string endpoint, string userEndpoint, string expected)
    {
        FakeCredentialStore store = new() { NextKey = "store-key" };

        string? resolved = ArbiterKeyResolver.Resolve(endpoint, store, EnvReader(SentinelKey), userEndpoint);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void StoreEntryServesWhenEnvAbsent()
    {
        FakeCredentialStore store = new() { NextKey = "store-key" };

        string? resolved = ArbiterKeyResolver.Resolve(
            "https://api.typesafe.ai/v1",
            store,
            EnvReader(null));

        Assert.Equal("store-key", resolved);
        Assert.Equal(TypeSafeOrigin, store.RequestedOrigins);
    }

    [Fact]
    public void AbsentEnvAndStoreResolvesNoKey()
    {
        FakeCredentialStore store = new() { NextKey = null };

        string? resolved = ArbiterKeyResolver.Resolve(
            "https://api.typesafe.ai/v1",
            store,
            EnvReader(null));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    public void LoopbackEndpointNeedsNoKey(string endpoint)
    {
        FakeCredentialStore store = new() { NextKey = "store-key" };
        bool envRead = false;
        Func<string, string?> env = (string name) =>
        {
            envRead = true;
            return SentinelKey;
        };

        string? resolved = ArbiterKeyResolver.Resolve(endpoint, store, env);

        Assert.Null(resolved);
        Assert.Empty(store.RequestedOrigins);
        Assert.False(envRead);
    }

    [Theory]
    // A hostname that merely begins "127." is not loopback. Reading it as loopback would
    // suppress the key for an endpoint the user's key must never reach.
    [InlineData("http://127.0.0.1.evil.com/v1")]
    [InlineData("http://127.0.evil/v1")]
    [InlineData("http://127.1.2.3.4.5/v1")]
    [InlineData("http://localhost.evil.com/v1")]
    public void LookalikeLoopbackEndpointStillResolvesTheKey(string endpoint)
    {
        FakeCredentialStore store = new() { NextKey = SentinelKey };

        string? resolved = ArbiterKeyResolver.Resolve(endpoint, store, EnvReader(SentinelKey));

        Assert.Equal(SentinelKey, resolved);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://LocalHost:11434/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://127.1.2.3:11434/v1")]
    [InlineData("http://[::1]:11434/v1")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:11434/v1")]
    public void GenuineLoopbackEndpointNeedsNoKey(string endpoint)
    {
        Assert.True(ArbiterKeyResolver.IsLoopbackEndpoint(endpoint));
    }

    [Fact]
    public void MacWriteKeepsKeyOnStdinOnly()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, string.Empty, string.Empty);
        MacKeychainCredentialStore store = new(runner);

        store.Write("https://api.typesafe.ai", SentinelKey);

        CapturedCall call = Assert.Single(runner.Calls);
        Assert.Equal("security", call.FileName);
        Assert.Equal(MacWriteArgv, call.Argv);
        Assert.Contains("add-generic-password", call.StandardInput, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", call.StandardInput, StringComparison.Ordinal);
        Assert.Contains("https://api.typesafe.ai", call.StandardInput, StringComparison.Ordinal);
        Assert.Contains(SentinelKey, call.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelKey, string.Join(" ", call.Argv), StringComparison.Ordinal);
    }

    [Fact]
    public void MacReadUsesExactArgvAndReturnsTrimmedStdout()
    {
        FakeCredentialProcessRunner runner = new();
        runner.NextResult = new ProcessResult(0, "stored-key\n", string.Empty);
        MacKeychainCredentialStore store = new(runner);

        string? key = store.Read("https://api.typesafe.ai");

        Assert.Equal("stored-key", key);
        CapturedCall call = Assert.Single(runner.Calls);
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

        CapturedCall call = Assert.Single(runner.Calls);
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
        CapturedCall call = Assert.Single(runner.Calls);
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

    [Fact]
    public void UserOverrideProviderOnlyReplacesFieldByField()
    {
        using TempDirectory home = new();
        string dir = Path.Combine(home.Path, ".config", "kyber-weave");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "arbiter.yml"), "provider:\n  model: nimble\n");

        ArbiterProviderConfig merged = ArbiterUserSettings.ApplyTo(ArbiterProviderConfig.DefaultNone, home.Path);

        Assert.Equal("nimble", merged.Model);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.Kind, merged.Kind);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.Endpoint, merged.Endpoint);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.TimeoutMs, merged.TimeoutMs);
    }

    [Fact]
    public void UserOverrideExtraKeyRaisesConfig009()
    {
        using TempDirectory home = new();
        string dir = Path.Combine(home.Path, ".config", "kyber-weave");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "arbiter.yml"), "provider:\n  model: nimble\nextra: 1\n");

        YamlDotNet.Core.YamlException exception = Assert.Throws<YamlDotNet.Core.YamlException>(
            () => ArbiterUserSettings.ApplyTo(ArbiterProviderConfig.DefaultNone, home.Path));

        Assert.Contains("KW-ARB-CONFIG-009", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserOverrideMalformedNamesFileWithConfig001()
    {
        using TempDirectory home = new();
        string dir = Path.Combine(home.Path, ".config", "kyber-weave");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "arbiter.yml");
        File.WriteAllText(path, "provider: [unclosed\n");

        YamlDotNet.Core.YamlException exception = Assert.Throws<YamlDotNet.Core.YamlException>(
            () => ArbiterUserSettings.ApplyTo(ArbiterProviderConfig.DefaultNone, home.Path));

        Assert.Contains("KW-ARB-CONFIG-001", exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingUserOverrideKeepsRepository()
    {
        using TempDirectory home = new();

        Assert.Equal(
            Path.Combine(home.Path, ".config", "kyber-weave", "arbiter.yml"),
            ArbiterUserSettings.GetPath(home.Path));
        Assert.Equal(
            ArbiterProviderConfig.DefaultNone,
            ArbiterUserSettings.ApplyTo(ArbiterProviderConfig.DefaultNone, home.Path));
    }

    [Fact]
    public void SentinelKeyLeaksIntoNoSurface()
    {
        FakeCredentialProcessRunner macRunner = new();
        MacKeychainCredentialStore mac = new(macRunner);
        mac.Write("https://api.typesafe.ai", SentinelKey);
        FakeCredentialProcessRunner linuxRunner = new();
        SecretServiceCredentialStore linux = new(linuxRunner);
        linux.Write("https://api.typesafe.ai", SentinelKey);
        WindowsCredentialStore windows = new();

        Assert.DoesNotContain(SentinelKey, mac.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelKey, linux.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelKey, windows.ToString() ?? string.Empty, StringComparison.Ordinal);

        FakeCredentialStore store = new() { NextKey = "store-key" };
        ArgumentException validationFailure = Assert.Throws<ArgumentException>(
            () => ArbiterKeyResolver.Resolve(string.Empty, store, EnvReader(SentinelKey)));
        Assert.DoesNotContain(SentinelKey, validationFailure.Message, StringComparison.Ordinal);

        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.ValidateUserOverride(
            new ArbiterYamlSection { Enabled = true },
            "arbiter.yml");
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Assert.DoesNotContain(SentinelKey, diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelKey, diagnostic.Hint ?? string.Empty, StringComparison.Ordinal);
        }

        foreach (Diagnostic diagnostic in RuleValidator.Validate(ArbiterConfig.ProductDefaults))
        {
            Assert.DoesNotContain(SentinelKey, diagnostic.ToString(), StringComparison.Ordinal);
        }
    }

    private sealed record CapturedCall(string FileName, string[] Argv, string StandardInput);

    private sealed class FakeCredentialProcessRunner : ICredentialProcessRunner
    {
        public List<CapturedCall> Calls { get; } = [];

        public ProcessResult NextResult { get; set; } = new(0, string.Empty, string.Empty);

        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
        {
            ArgumentNullException.ThrowIfNull(startInfo);
            ArgumentNullException.ThrowIfNull(standardInput);
            string[] argv = [.. startInfo.ArgumentList];
            Calls.Add(new CapturedCall(startInfo.FileName, argv, standardInput));
            return NextResult;
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public List<string> RequestedOrigins { get; } = [];

        public string? NextKey { get; set; }

        public string? Read(string origin)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            RequestedOrigins.Add(origin);
            return NextKey;
        }

        public void Write(string origin, string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            ArgumentException.ThrowIfNullOrEmpty(key);
            RequestedOrigins.Add(origin);
        }
    }
}
