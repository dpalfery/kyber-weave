using KyberWeave.Data.Configuration;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Configuration;

/// <summary>The user host-config store over an in-memory file system.</summary>
public sealed class FileUserHostConfigStoreTests
{
    private const string Home = "/home/dev";
    private const string ConfigPath = Home + "/.kyber-weave/kyber-weave.yml";

    private readonly InMemoryFileSystem _files = new();

    private FileUserHostConfigStore Store() => new(_files);

    [Fact]
    public void ACreatedConfigEnablesTheArbiterAndIsWrittenAtTheUserHome()
    {
        string yaml = Store().EnsureUserConfig(Home);

        Assert.True(_files.FileExists(ConfigPath));
        Assert.Equal(yaml, _files.ReadAllText(ConfigPath));
        Assert.Contains("enabled: true", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordedGlobalTargetsReadBackDistinctAndOneCanBeRemoved()
    {
        Store().WriteGlobalTargets(Home, ["claude", "warp", "claude"]);

        Assert.Equal(["claude", "warp"], Store().ReadGlobalTargets(Home));

        Store().RemoveGlobalTarget(Home, "claude");

        Assert.Equal(["warp"], Store().ReadGlobalTargets(Home));
    }

    [Fact]
    public void AMissingOrUnparsableConfigReadsNoTargets()
    {
        Assert.Empty(Store().ReadGlobalTargets(Home));

        _files.CreateDirectory(Home + "/.kyber-weave");
        _files.WriteAllText(ConfigPath, "arbiter: [unclosed");

        Assert.Empty(Store().ReadGlobalTargets(Home));
    }

    [Fact]
    public void RemovingATargetFromAMissingConfigLeavesItMissing()
    {
        Store().RemoveGlobalTarget(Home, "claude");

        Assert.False(_files.FileExists(ConfigPath));
    }
}
