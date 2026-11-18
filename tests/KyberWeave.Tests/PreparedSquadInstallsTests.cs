using KyberWeave.Core.Squad.Deployment;
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

    /// <summary>
    /// A preset's body is an input the prepared install consumes, so two recipes that name the same
    /// presets but write different bodies must not share one install: the update under test reads the
    /// preset the install recorded, and a shared install would hand it the other recipe's bytes.
    /// </summary>
    [Fact]
    public void APresetRecipeWithADifferentPresetBodyGetsItsOwnInstall()
    {
        string first = SquadInstalledPresetProjectTemplate.RecipeKey(
            "preset-body",
            [SquadTarget.Copilot],
            PresetNames,
            [("fast", "schema: kyber-squad.model-preset/v1\nmodels:\n  general: one\n")]);
        string second = SquadInstalledPresetProjectTemplate.RecipeKey(
            "preset-body",
            [SquadTarget.Copilot],
            PresetNames,
            [("fast", "schema: kyber-squad.model-preset/v1\nmodels:\n  general: two\n")]);

        Assert.NotEqual(first, second);

        string same = SquadInstalledPresetProjectTemplate.RecipeKey(
            "preset-body",
            [SquadTarget.Copilot],
            PresetNames,
            [("fast", "schema: kyber-squad.model-preset/v1\nmodels:\n  general: one\n")]);
        Assert.Equal(first, same);
    }

    private static readonly IReadOnlyDictionary<SquadTarget, string> PresetNames =
        new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "fast" };

    /// <summary>
    /// A build that fails is not an install: the recipe is left unbuilt so the next request tries
    /// again, rather than every later test in the run rethrowing the one transient failure.
    /// </summary>
    [Fact]
    public void AFailedBuildIsNotCachedSoTheNextRequestBuildsIt()
    {
        string key = NewKey("retry");
        _ = Assert.Throws<InvalidOperationException>(() =>
            PreparedSquadInstalls.Get<PreparedSquadInstall>(key, () => throw new InvalidOperationException("transient")));

        PreparedSquadInstall install = PreparedSquadInstalls.Get(key, () => new PreparedSquadInstall(key, Seeded(key)));

        Assert.Equal(key, install.Key);
        Assert.Equal(1, PreparedSquadInstalls.BuildsOf(key));
    }

    /// <summary>
    /// Removal runs on process exit and sweeps every recipe, so a recipe whose build failed must not
    /// throw out of the sweep: there is nothing to dispose, and the sweep continues.
    /// </summary>
    [Fact]
    public void RemovingARecipeWhoseBuildFailedDoesNotThrow()
    {
        string key = NewKey("faulted-cleanup");
        _ = Assert.Throws<InvalidOperationException>(() =>
            PreparedSquadInstalls.Get<PreparedSquadInstall>(key, () => throw new InvalidOperationException("transient")));

        PreparedSquadInstalls.Remove(key);

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
