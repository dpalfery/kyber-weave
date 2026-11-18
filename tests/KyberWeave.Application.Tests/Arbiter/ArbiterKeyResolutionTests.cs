using KyberWeave.Application.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Arbiter.Credentials;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.8 (key resolution): the <c>TYPESAFE_API_KEY</c> origin binding, the per-user provider override
/// and its diagnostics, and the no-leak guarantee. The per-OS stores are in <c>ArbiterCredentialStoreArgvTests</c>.
/// </summary>
public sealed class ArbiterKeyResolutionTests
{
    private const string SentinelKey = "TS-SENTINEL-KEY-9f8e7d6c5b4a";

    private static readonly string[] ForeignOrigin = ["https://other.example.com"];

    private static readonly string[] TypeSafeOrigin = ["https://api.typesafe.ai"];

    private readonly InMemoryArbiterState _state = new();

    private static Func<string, string?> EnvReader(string? value) =>
        (string name) => string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? value : null;

    [Fact]
    public void EnvKeyServesTypeSafeOriginWithoutTouchingStore()
    {
        FakeCredentialStore store = new() { DefaultKey = "store-key" };

        string? resolved = ArbiterKeyResolver.Resolve(
            "https://api.typesafe.ai/v1",
            store,
            EnvReader(SentinelKey));

        Assert.Equal(SentinelKey, resolved);
        Assert.Empty(store.Reads);
    }

    [Fact]
    public void EnvKeyIgnoredForForeignOrigin()
    {
        FakeCredentialStore store = new() { DefaultKey = "store-key" };

        string? resolved = ArbiterKeyResolver.Resolve(
            "https://other.example.com/v1",
            store,
            EnvReader(SentinelKey));

        Assert.Equal("store-key", resolved);
        Assert.Equal(ForeignOrigin, store.Reads);
    }

    [Theory]
    [InlineData("https://proxy.example.com/v1", "https://proxy.example.com/v1", SentinelKey)]
    [InlineData("https://proxy.example.com/v1", "https://elsewhere.example.com/v1", "store-key")]
    public void EnvKeyHonoredOnlyForUserOverrideOrigin(string endpoint, string userEndpoint, string expected)
    {
        FakeCredentialStore store = new() { DefaultKey = "store-key" };

        string? resolved = ArbiterKeyResolver.Resolve(endpoint, store, EnvReader(SentinelKey), userEndpoint);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void StoreEntryServesWhenEnvAbsent()
    {
        FakeCredentialStore store = new() { DefaultKey = "store-key" };

        string? resolved = ArbiterKeyResolver.Resolve(
            "https://api.typesafe.ai/v1",
            store,
            EnvReader(null));

        Assert.Equal("store-key", resolved);
        Assert.Equal(TypeSafeOrigin, store.Reads);
    }

    [Fact]
    public void AbsentEnvAndStoreResolvesNoKey()
    {
        FakeCredentialStore store = new();

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
        FakeCredentialStore store = new() { DefaultKey = "store-key" };
        bool envRead = false;
        Func<string, string?> env = (string name) =>
        {
            envRead = true;
            return SentinelKey;
        };

        string? resolved = ArbiterKeyResolver.Resolve(endpoint, store, env);

        Assert.Null(resolved);
        Assert.Empty(store.Reads);
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
        FakeCredentialStore store = new() { DefaultKey = SentinelKey };

        string? resolved = ArbiterKeyResolver.Resolve(endpoint, store, EnvReader(SentinelKey));

        Assert.Equal(SentinelKey, resolved);
    }

    [Theory]
    // `localhost.` is the absolute spelling of `localhost` and names the same interface.
    // RuleValidator asks the same helper, so a spelling one accepts and the other refuses
    // is a configuration the validator permits and the key resolver then withholds a key
    // from, which is the disagreement this rules out.
    [InlineData("http://localhost.:11434/v1")]
    [InlineData("http://LOCALHOST.:11434/v1")]
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
    public void UserOverrideProviderOnlyReplacesFieldByField()
    {
        string home = ArbiterTestRoots.Under("home");
        _state.Files.CreateDirectory(Path.Combine(home, ".config", "kyber-weave"));
        _state.Files.WriteAllText(ArbiterUserSettings.GetPath(home), "provider:\n  model: nimble\n");

        ArbiterProviderConfig merged = new FileArbiterUserSettings(_state.Files).ApplyTo(ArbiterProviderConfig.DefaultNone, home);

        Assert.Equal("nimble", merged.Model);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.Kind, merged.Kind);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.Endpoint, merged.Endpoint);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.TimeoutMs, merged.TimeoutMs);
    }

    [Fact]
    public void UserOverrideExtraKeyRaisesConfig009()
    {
        string home = ArbiterTestRoots.Under("home");
        _state.Files.CreateDirectory(Path.Combine(home, ".config", "kyber-weave"));
        _state.Files.WriteAllText(ArbiterUserSettings.GetPath(home), "provider:\n  model: nimble\nextra: 1\n");

        YamlDotNet.Core.YamlException exception = Assert.Throws<YamlDotNet.Core.YamlException>(
            () => new FileArbiterUserSettings(_state.Files).ApplyTo(ArbiterProviderConfig.DefaultNone, home));

        Assert.Contains("KW-ARB-CONFIG-009", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserOverrideMalformedNamesFileWithConfig001()
    {
        string home = ArbiterTestRoots.Under("home");
        _state.Files.CreateDirectory(Path.Combine(home, ".config", "kyber-weave"));
        string path = ArbiterUserSettings.GetPath(home);
        _state.Files.WriteAllText(path, "provider: [unclosed\n");

        YamlDotNet.Core.YamlException exception = Assert.Throws<YamlDotNet.Core.YamlException>(
            () => new FileArbiterUserSettings(_state.Files).ApplyTo(ArbiterProviderConfig.DefaultNone, home));

        Assert.Contains("KW-ARB-CONFIG-001", exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingUserOverrideKeepsRepository()
    {
        string home = ArbiterTestRoots.Under("home");

        Assert.Equal(
            Path.Combine(home, ".config", "kyber-weave", "arbiter.yml"),
            ArbiterUserSettings.GetPath(home));
        Assert.Equal(
            ArbiterProviderConfig.DefaultNone,
            new FileArbiterUserSettings(_state.Files).ApplyTo(ArbiterProviderConfig.DefaultNone, home));
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

        FakeCredentialStore store = new() { DefaultKey = "store-key" };
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

        foreach (Diagnostic diagnostic in RuleValidator.Validate(ArbiterConfigDefaults.Product))
        {
            Assert.DoesNotContain(SentinelKey, diagnostic.ToString(), StringComparison.Ordinal);
        }
    }
}
