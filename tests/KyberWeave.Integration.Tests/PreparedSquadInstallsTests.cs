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

    [Fact]
    public void DifferentInstallSubtypesSharingAKeyDoNotCollide()
    {
        string key = NewKey("subtype-isolation");

        FirstSubtype first = PreparedSquadInstalls.Get(key, () => new FirstSubtype(key, Seeded(key)));
        SecondSubtype second = PreparedSquadInstalls.Get(key, () => new SecondSubtype(key, Seeded(key)));

        Assert.NotSame(first, second);
        Assert.Equal(2, PreparedSquadInstalls.BuildsOf(key));
    }

    /// <summary>
    /// A factory that returns null must fail at the point of the request, and must not leave a build
    /// count behind that would read as a successful install.
    /// </summary>
    [Fact]
    public void NullFactoryResultThrowsInvalidOperationWithoutIncrementingBuildCount()
    {
        string key = NewKey("null-factory");

        _ = Assert.Throws<InvalidOperationException>(
            () => PreparedSquadInstalls.Get<PreparedSquadInstall>(key, () => null!));

        Assert.Equal(0, PreparedSquadInstalls.BuildsOf(key));
    }

    /// <summary>
    /// A copy keeps symbolic links as links and keeps the Unix mode that makes a deployed hook runnable,
    /// so a test deploying through the copy behaves as the real install does.
    /// </summary>
    [Fact]
    public void TreePreservesSymlinksAndUnixExecutableMode()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string targetFile = Path.Combine(source.Path, "hook.sh");
        File.WriteAllText(targetFile, "#!/bin/sh\necho ok\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                targetFile,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string fileLink = Path.Combine(source.Path, "hook-link.sh");
        File.CreateSymbolicLink(fileLink, "hook.sh");
        string realDir = Directory.CreateDirectory(Path.Combine(source.Path, "real-dir")).FullName;
        File.WriteAllText(Path.Combine(realDir, "inside.txt"), "inside");
        string dirLink = Path.Combine(source.Path, "dir-link");
        Directory.CreateSymbolicLink(dirLink, "real-dir");

        SquadInstallCopy.Tree(source.Path, destination.Path);

        Assert.Equal("hook.sh", new FileInfo(Path.Combine(destination.Path, "hook-link.sh")).LinkTarget);
        Assert.Equal("real-dir", new DirectoryInfo(Path.Combine(destination.Path, "dir-link")).LinkTarget);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode copiedMode = File.GetUnixFileMode(Path.Combine(destination.Path, "hook.sh"));
            Assert.True(copiedMode.HasFlag(UnixFileMode.UserExecute));
        }
    }

    /// <summary>
    /// A copy keeps a directory link as a link, so re-rooting must stop at the copy's own files: the
    /// link is not the copy's content, and following it would rewrite a file outside the sandbox.
    /// </summary>
    [Fact]
    public void ReRootDoesNotFollowADirectorySymlinkOutOfTheCopy()
    {
        using TempDirectory temp = new();
        using TempDirectory outside = new();
        string outsideFile = Path.Combine(outside.Path, "settings.json");
        string original = $$"""{"root":"{{From}}/a"}""";
        File.WriteAllText(outsideFile, original);
        string stateDir = Directory.CreateDirectory(Path.Combine(temp.Path, "state")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(stateDir, "escape"), outside.Path);

        SquadInstallCopy.ReRoot(stateDir, From, To);

        Assert.Equal(original, File.ReadAllText(outsideFile));
    }

    /// <summary>
    /// Re-rooting touches only the recorded root itself: a binary file that happens to contain the
    /// bytes is left alone, and a sibling path that merely starts with the root is not rewritten.
    /// </summary>
    [Fact]
    public void ReRootSkipsBinariesAndSiblingPrefixMatches()
    {
        using TempDirectory temp = new();
        string stateDir = Directory.CreateDirectory(Path.Combine(temp.Path, "state")).FullName;
        string from = From;
        string to = To;
        byte[] binaryBytes = [.. System.Text.Encoding.UTF8.GetBytes(from), 0x00, 0x01, 0xFF];
        string binaryPath = Path.Combine(stateDir, "cache.db");
        File.WriteAllBytes(binaryPath, binaryBytes);

        string jsonPath = Path.Combine(stateDir, "receipt.json");
        File.WriteAllText(jsonPath, $$"""{"self":"{{from}}/a","sibling":"/tmp/kw-k/123/b"}""");

        SquadInstallCopy.ReRoot(stateDir, from, to);

        Assert.Equal(binaryBytes, File.ReadAllBytes(binaryPath));
        Assert.Equal($$"""{"self":"{{to}}/a","sibling":"/tmp/kw-k/123/b"}""", File.ReadAllText(jsonPath));
    }

    private sealed class FirstSubtype(string key, TempDirectory directory) : PreparedSquadInstall(key, directory);

    private sealed class SecondSubtype(string key, TempDirectory directory) : PreparedSquadInstall(key, directory);

    private const string From = "/tmp/kw-k/12";

    private const string To = "/tmp/kw-k/99";

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
