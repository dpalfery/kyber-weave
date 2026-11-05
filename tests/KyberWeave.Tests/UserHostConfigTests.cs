using System.Text;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for <c>arbiter.global</c> user-level host config installation and merging.
/// </summary>
public sealed class UserHostConfigTests
{
    [Fact]
    public void CreatesUserConfigWhenMissingWithArbiterEnabledAtTheUserHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "kyber-weave-user-host-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        string path = UserHostConfig.GetPath(home);
        string yaml = UserHostConfig.EnsureUserConfig(home);

        Assert.Equal(Path.Combine(home, ".kyber-weave", "kyber-weave.yml"), path);
        Assert.True(File.Exists(path));
        Assert.Contains("arbiter:", yaml, StringComparison.Ordinal);
        Assert.Contains("enabled: true", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void UserConfigMergeIsIdempotentAndPreservesCommentsAndKeyOrder()
    {
        string home = Path.Combine(Path.GetTempPath(), "kyber-weave-user-host-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        string path = UserHostConfig.GetPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "# keep this comment\n" +
            "ontology:\n" +
            "  docs-root: docs\n" +
            "arbiter:\n" +
            "  enabled: false\n" +
            "  provider:\n" +
            "    kind: none\n");

        string yaml = UserHostConfig.EnsureUserConfig(home);

        Assert.Contains("# keep this comment", yaml, StringComparison.Ordinal);
        Assert.Contains("ontology:", yaml, StringComparison.Ordinal);
        Assert.Contains("docs-root: docs", yaml, StringComparison.Ordinal);
        Assert.Contains("enabled: true", yaml, StringComparison.Ordinal);
        Assert.Contains("provider:\n    kind: none", yaml, StringComparison.Ordinal);
        Assert.Equal(yaml, UserHostConfig.EnsureUserConfig(home));
    }

    [Fact]
    public void UserConfigWritesAreAtomicAndLeaveNoTempFiles()
    {
        string home = Path.Combine(Path.GetTempPath(), "kyber-weave-user-host-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        string path = UserHostConfig.GetPath(home);
        string configDirectory = Path.GetDirectoryName(path)!;

        UserHostConfig.EnsureUserConfig(home);
        UserHostConfig.WriteGlobalTargets(home, ["harness-token-01", "harness-token-02"]);

        string expected = "arbiter:\n" +
                          "  enabled: true\n" +
                          "  global:\n" +
                          "    targets:\n" +
                          "      - 'harness-token-01'\n" +
                          "      - 'harness-token-02'\n";

        Assert.Equal(expected, File.ReadAllText(path));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(configDirectory),
            file => Path.GetFileName(file).Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WritesGlobalTargetsToTheUserConfigAndReadsThemBack()
    {
        string home = Path.Combine(Path.GetTempPath(), "kyber-weave-user-host-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        UserHostConfig.WriteGlobalTargets(home, ["harness-token-01", "harness-token-02"]);

        string yaml = File.ReadAllText(UserHostConfig.GetPath(home));
        Assert.Contains("arbiter:", yaml, StringComparison.Ordinal);
        Assert.Contains("global:", yaml, StringComparison.Ordinal);
        Assert.Contains("targets:", yaml, StringComparison.Ordinal);
        Assert.Contains("harness-token-01", yaml, StringComparison.Ordinal);
        Assert.Contains("harness-token-02", yaml, StringComparison.Ordinal);
        Assert.Equal(["harness-token-01", "harness-token-02"], UserHostConfig.ReadGlobalTargets(home));
    }
}
