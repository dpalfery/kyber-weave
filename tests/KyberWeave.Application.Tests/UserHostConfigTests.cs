using KyberWeave.Data.Configuration;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for <c>arbiter.global</c> user-level host config installation and merging.
/// </summary>
public sealed class UserHostConfigTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly FileUserHostConfigStore _store;

    public UserHostConfigTests() => _store = new FileUserHostConfigStore(_files);

    private string NewHome()
    {
        string home = Path.Combine(_files.Root, "home");
        _files.CreateDirectory(home);
        return home;
    }

    [Fact]
    public void CreatesUserConfigWhenMissingWithArbiterEnabledAtTheUserHome()
    {
        string home = NewHome();

        string path = UserHostConfigPath.Of(home);
        string yaml = _store.EnsureUserConfig(home);

        Assert.Equal(Path.Combine(home, ".kyber-weave", "kyber-weave.yml"), path);
        Assert.True(_files.FileExists(path));
        Assert.Contains("arbiter:", yaml, StringComparison.Ordinal);
        Assert.Contains("enabled: true", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void UserConfigMergeIsIdempotentAndPreservesCommentsAndKeyOrder()
    {
        string home = NewHome();

        string path = UserHostConfigPath.Of(home);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path,
            "# keep this comment\n" +
            "ontology:\n" +
            "  docs-root: docs\n" +
            "arbiter:\n" +
            "  enabled: false\n" +
            "  provider:\n" +
            "    kind: none\n");

        string yaml = _store.EnsureUserConfig(home);

        Assert.Contains("# keep this comment", yaml, StringComparison.Ordinal);
        Assert.Contains("ontology:", yaml, StringComparison.Ordinal);
        Assert.Contains("docs-root: docs", yaml, StringComparison.Ordinal);
        Assert.Contains("enabled: true", yaml, StringComparison.Ordinal);
        Assert.Contains("provider:\n    kind: none", yaml, StringComparison.Ordinal);
        Assert.Equal(yaml, _store.EnsureUserConfig(home));
    }

    [Fact]
    public void UserConfigWritesAreAtomicAndLeaveNoTempFiles()
    {
        string home = NewHome();

        string path = UserHostConfigPath.Of(home);
        string configDirectory = Path.GetDirectoryName(path)!;

        _store.EnsureUserConfig(home);
        _store.WriteGlobalTargets(home, ["harness-token-01", "harness-token-02"]);

        string expected = "arbiter:\n" +
                          "  enabled: true\n" +
                          "  global:\n" +
                          "    targets:\n" +
                          "      - 'harness-token-01'\n" +
                          "      - 'harness-token-02'\n";

        Assert.Equal(expected, _files.ReadAllText(path));
        Assert.DoesNotContain(
            _files.EnumerateFiles(configDirectory, "*", recursive: false),
            file => Path.GetFileName(file).Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WritesGlobalTargetsToTheUserConfigAndReadsThemBack()
    {
        string home = NewHome();

        _store.WriteGlobalTargets(home, ["harness-token-01", "harness-token-02"]);

        string yaml = _files.ReadAllText(UserHostConfigPath.Of(home));
        Assert.Contains("arbiter:", yaml, StringComparison.Ordinal);
        Assert.Contains("global:", yaml, StringComparison.Ordinal);
        Assert.Contains("targets:", yaml, StringComparison.Ordinal);
        Assert.Contains("harness-token-01", yaml, StringComparison.Ordinal);
        Assert.Contains("harness-token-02", yaml, StringComparison.Ordinal);
        Assert.Equal(["harness-token-01", "harness-token-02"], _store.ReadGlobalTargets(home));
    }
}
