using KyberWeave.Application.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using Xunit;

namespace KyberWeave.Integration.Tests.Arbiter;

/// <summary>Smoke test: the per-user provider override is read from a real <c>arbiter.yml</c> on disk through the real file system.</summary>
public sealed class ArbiterUserSettingsDiskTests
{
    [Fact]
    public void UserOverrideProviderOnlyReplacesFieldByField()
    {
        using TempDirectory home = new();
        string directory = Path.Combine(home.Path, ".config", "kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(ArbiterUserSettings.GetPath(home.Path), "provider:\n  model: nimble\n");

        ArbiterProviderConfig merged = new FileArbiterUserSettings(new PhysicalFileSystem()).ApplyTo(ArbiterProviderConfig.DefaultNone, home.Path);

        Assert.Equal("nimble", merged.Model);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.Kind, merged.Kind);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.Endpoint, merged.Endpoint);
        Assert.Equal(ArbiterProviderConfig.DefaultNone.TimeoutMs, merged.TimeoutMs);
    }
}
