using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the run-wide prepared install: one build per recipe under concurrent requests, copies that are
/// isolated from the template and from each other, recorded paths re-rooted into the copy, and removal
/// when the run ends.
/// </summary>
public sealed class PreparedSquadInstallsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<string> _keys = [];

    public void Dispose()
    {
        foreach (string key in _keys)
            PreparedSquadInstalls.Remove(key);

        _temp.Dispose();
    }

    [Fact]
    public void ConcurrentRequestsForOneRecipeBuildTheInstallOnce()
    {
        string key = NewKey("concurrent");
        int builds = 0;
        PreparedSquadInstall[] results = new PreparedSquadInstall[16];

        _ = Parallel.For(0, results.Length, index =>
            results[index] = PreparedSquadInstalls.Get(key, () =>
            {
                _ = Interlocked.Increment(ref builds);
                return new PreparedSquadInstall(key, Seeded(key));
            }));

        Assert.Equal(1, builds);
        Assert.Equal(1, PreparedSquadInstalls.BuildsOf(key));
        Assert.All(results, install => Assert.Same(results[0], install));
    }

    [Fact]
    public void ACopyIsIsolatedFromTheTemplateAndFromAnotherCopy()
    {
        string key = NewKey("isolation");
        PreparedSquadInstall template = PreparedSquadInstalls.Get(key, () => new PreparedSquadInstall(key, Seeded(key)));
        string original = File.ReadAllText(Path.Combine(template.TemplatePath, "deployed.txt"));

        string first = Path.Combine(_temp.Path, "first");
        string second = Path.Combine(_temp.Path, "second");
        SquadInstallCopy.Tree(template.TemplatePath, first);
        SquadInstallCopy.Tree(template.TemplatePath, second);

        File.WriteAllText(Path.Combine(first, "deployed.txt"), "edited by the first test");

        Assert.Equal(original, File.ReadAllText(Path.Combine(template.TemplatePath, "deployed.txt")));
        Assert.Equal(original, File.ReadAllText(Path.Combine(second, "deployed.txt")));
    }

    [Fact]
    public void ACopyReRootsThePathRecordedInState()
    {
        string key = NewKey("re-root");
        PreparedSquadInstall template = PreparedSquadInstalls.Get(key, () => new PreparedSquadInstall(key, Seeded(key)));
        string destination = Path.Combine(_temp.Path, "copy");

        SquadInstallCopy.Tree(template.TemplatePath, destination);
        SquadInstallCopy.ReRoot(
            Path.Combine(destination, "state"),
            SquadInstallCopy.Canonical(template.TemplatePath),
            SquadInstallCopy.Canonical(destination));

        string state = File.ReadAllText(Path.Combine(destination, "state", "receipt.json"));
        Assert.Contains(SquadInstallCopy.Canonical(destination), state, StringComparison.Ordinal);
        Assert.DoesNotContain(SquadInstallCopy.Canonical(template.TemplatePath), state, StringComparison.Ordinal);
    }

    [Fact]
    public void ACopyLeavesDeployedBytesAloneBecauseTheReceiptDigestNamesThem()
    {
        string key = NewKey("deployed");
        PreparedSquadInstall template = PreparedSquadInstalls.Get(key, () => new PreparedSquadInstall(key, Seeded(key)));
        string destination = Path.Combine(_temp.Path, "copy");
        string deployed = File.ReadAllText(Path.Combine(template.TemplatePath, "deployed.txt"));

        SquadInstallCopy.Tree(template.TemplatePath, destination);
        SquadInstallCopy.ReRoot(
            Path.Combine(destination, "state"),
            SquadInstallCopy.Canonical(template.TemplatePath),
            SquadInstallCopy.Canonical(destination));

        Assert.Equal(deployed, File.ReadAllText(Path.Combine(destination, "deployed.txt")));
    }

    [Fact]
    public void RemovingAnInstallDeletesItsDirectory()
    {
        string key = NewKey("cleanup");
        PreparedSquadInstall install = PreparedSquadInstalls.Get(key, () => new PreparedSquadInstall(key, Seeded(key)));
        string path = install.TemplatePath;
        Assert.True(Directory.Exists(path));

        PreparedSquadInstalls.Remove(key);

        Assert.False(Directory.Exists(path));
        Assert.Equal(0, PreparedSquadInstalls.BuildsOf(key));
    }

    private string NewKey(string purpose)
    {
        string key = $"prepared-installs-tests/{purpose}/{Guid.NewGuid():N}";
        _keys.Add(key);
        return key;
    }

    /// <summary>
    /// A stand-in install: one deployed file and one state file that records the install's own root, which
    /// is the shape the copy helper has to handle. The provider does not care how the install was made.
    /// </summary>
    private static TempDirectory Seeded(string key)
    {
        TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "deployed.txt"), $"deployed from {key}");
        string state = Directory.CreateDirectory(Path.Combine(directory.Path, "state")).FullName;
        File.WriteAllText(
            Path.Combine(state, "receipt.json"),
            $$"""{"targetRoot":"{{SquadInstallCopy.Canonical(directory.Path)}}"}""");
        return directory;
    }
}
