using KyberWeave.Data.Arbiter;
using KyberWeave.Core.Arbiter;
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
}
