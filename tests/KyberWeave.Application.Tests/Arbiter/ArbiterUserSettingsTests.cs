using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The per-user provider override is read from an <c>arbiter.yml</c> in the user's home through the file system seam.</summary>
public sealed class ArbiterUserSettingsTests
{
    [Fact]
    public void UserOverrideProviderOnlyReplacesFieldByField()
    {
        InMemoryFileSystem files = new();
        string home = Path.Combine(files.Root, "home");
        files.CreateDirectory(Path.Combine(home, ".config", "kyber-weave"));
        files.WriteAllText(ArbiterUserSettings.GetPath(home), "provider:\n  model: nimble\n");

        ArbiterProviderConfig merged = new FileArbiterUserSettings(files).ApplyTo(ArbiterProviderConfig.DefaultNone, home);

        Assert.Equal("nimble", merged.Model);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.Kind, merged.Kind);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.Endpoint, merged.Endpoint);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.TimeoutMs, merged.TimeoutMs);
    }

    [Fact]
    public void TryResolveEndpointOrigin_WhitespaceOnlyDifference_NamesHostConfig()
    {
        const string hostConfigPath = "/repo/.kyber-weave/kyber-weave.yml";
        const string home = "/home/user";
        ArbiterProviderConfig repository = new(ArbiterProviderKind.Systemone, " not-a-url ", "jev-1.14.0", 3000);
        ArbiterProviderConfig effective = new(ArbiterProviderKind.Systemone, "not-a-url", "jev-1.14.0", 3000);

        bool resolved = ArbiterUserSettings.TryResolveEndpointOrigin(
            repository,
            effective,
            home,
            hostConfigPath,
            out string endpoint,
            out string origin,
            out string? error);

        Assert.False(resolved);
        Assert.Equal(string.Empty, endpoint);
        Assert.Equal(string.Empty, origin);
        Assert.NotNull(error);
        Assert.Contains(hostConfigPath, error, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterUserSettings.GetPath(home), error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveEndpointOrigin_LongInvalidEndpoint_TruncatesCandidateInError()
    {
        const string hostConfigPath = "/repo/.kyber-weave/kyber-weave.yml";
        const string home = "/home/user";
        string longInvalid = new('x', 250);
        ArbiterProviderConfig repository = new(ArbiterProviderKind.Systemone, longInvalid, "jev-1.14.0", 3000);

        bool resolved = ArbiterUserSettings.TryResolveEndpointOrigin(
            repository,
            repository,
            home,
            hostConfigPath,
            out _,
            out _,
            out string? error);

        Assert.False(resolved);
        Assert.NotNull(error);
        Assert.Contains(RuleValidator.MalformedSection, error, StringComparison.Ordinal);
        Assert.Contains(new string('x', 200) + "...", error, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 201), error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveEndpointOrigin_LongValidEndpoint_ReturnsFullTrimmedEndpoint()
    {
        const string home = "/home/user";
        string trimmedValid = "https://openrouter.ai/api/" + new string('a', 220);
        ArbiterProviderConfig repository = new(ArbiterProviderKind.Systemone, $"  {trimmedValid}  ", "jev-1.14.0", 3000);

        bool resolved = ArbiterUserSettings.TryResolveEndpointOrigin(
            repository,
            repository,
            home,
            configPath: null,
            out string endpoint,
            out string origin,
            out string? error);

        Assert.True(resolved);
        Assert.Null(error);
        Assert.Equal(trimmedValid, endpoint);
        Assert.Equal("https://openrouter.ai", origin);
    }

    [Fact]
    public void ResolveAndTryResolveEndpointOrigin_SameInvalidEndpointInOverrideAndHost_NamesOverrideFile()
    {
        const string hostConfigPath = "/repo/.kyber-weave/kyber-weave.yml";
        InMemoryFileSystem files = new();
        string home = Path.Combine(files.Root, "home");
        string overridePath = ArbiterUserSettings.GetPath(home);
        files.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        files.WriteAllText(overridePath, "provider:\n  endpoint: not-a-url\n");
        ArbiterProviderConfig repository = new(ArbiterProviderKind.Systemone, "not-a-url", "jev-1.14.0", 3000);

        ArbiterUserProviderResolution resolution =
            new FileArbiterUserSettings(files).Resolve(repository, home);

        Assert.True(resolution.EndpointOverridden);
        bool resolved = ArbiterUserSettings.TryResolveEndpointOrigin(
            repository,
            resolution.Provider,
            resolution.EndpointOverridden,
            home,
            hostConfigPath,
            out _,
            out _,
            out string? error);

        Assert.False(resolved);
        Assert.NotNull(error);
        Assert.Contains(overridePath, error, StringComparison.Ordinal);
        Assert.DoesNotContain(hostConfigPath, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAndTryResolveEndpointOrigin_OverrideWithoutEndpointAndInvalidHost_NamesHostConfig()
    {
        const string hostConfigPath = "/repo/.kyber-weave/kyber-weave.yml";
        InMemoryFileSystem files = new();
        string home = Path.Combine(files.Root, "home");
        string overridePath = ArbiterUserSettings.GetPath(home);
        files.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        files.WriteAllText(overridePath, "provider:\n  model: nimble\n");
        ArbiterProviderConfig repository = new(ArbiterProviderKind.Systemone, "not-a-url", "jev-1.14.0", 3000);

        ArbiterUserProviderResolution resolution =
            new FileArbiterUserSettings(files).Resolve(repository, home);

        Assert.False(resolution.EndpointOverridden);
        bool resolved = ArbiterUserSettings.TryResolveEndpointOrigin(
            repository,
            resolution.Provider,
            resolution.EndpointOverridden,
            home,
            hostConfigPath,
            out _,
            out _,
            out string? error);

        Assert.False(resolved);
        Assert.NotNull(error);
        Assert.Contains(hostConfigPath, error, StringComparison.Ordinal);
        Assert.DoesNotContain(overridePath, error, StringComparison.Ordinal);
    }
}
