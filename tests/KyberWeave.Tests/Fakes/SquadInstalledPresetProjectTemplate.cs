using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// One real project install that recorded a model preset, built once for the whole run and copied into
/// each test's own project root and fake home.
/// </summary>
/// <remarks>
/// <see cref="SquadModelPresetLifecycleTests"/> exercises what an update does with the preset the lock
/// records, and the install that recorded it is only the starting point. A real install pays the
/// deployment transaction for every rendered file, so the recipe is built here through the real lifecycle
/// once for the run and never written again. <see cref="CopyInto"/> copies the project tree and the whole
/// home (the project's state directory, the recorded lock and any canonical profile snapshot) and keys the
/// copied state directory to the copy's own root, so the update reads a deployment that looks like one the
/// test made itself.
/// </remarks>
public sealed class SquadInstalledPresetProjectTemplate
{
    private readonly string _projectRoot;
    private readonly string _homeDirectory;

    /// <summary>Gets the shared install for <paramref name="recipe"/>, building it on first request.</summary>
    /// <param name="recipe">Names the preset map, so two recipes never share one install.</param>
    /// <param name="targets">The targets the install deploys.</param>
    /// <param name="presetNames">The preset each target records in the lock.</param>
    /// <param name="presets">The preset files the install writes into the home.</param>
    public SquadInstalledPresetProjectTemplate(
        string recipe,
        SquadTarget[] targets,
        IReadOnlyDictionary<SquadTarget, string> presetNames,
        params (string Name, string Yaml)[] presets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(presetNames);
        ArgumentNullException.ThrowIfNull(presets);

        string key = RecipeKey(recipe, targets, presetNames, presets);
        Install install = PreparedSquadInstalls.Get(key, () => Build(key, targets, presetNames, presets));
        _projectRoot = install.ProjectRoot;
        _homeDirectory = install.HomeDirectory;
    }

    /// <summary>The shared install itself, which owns the directory and removes it when the run ends.</summary>
    private sealed class Install(string key, TempDirectory directory, string projectRoot, string homeDirectory)
        : PreparedSquadInstall(key, directory)
    {
        public string ProjectRoot { get; } = projectRoot;

        public string HomeDirectory { get; } = homeDirectory;
    }

    // The install belongs to PreparedSquadInstalls, which removes it when the run ends, so a test that
    // copies the template has nothing of its own to dispose.

    /// <summary>
    /// Gives <paramref name="targetRoot"/> this template's deployment and <paramref name="userPaths"/> its
    /// recorded state, as if the test had installed it there itself.
    /// </summary>
    /// <param name="userPaths">The test's fake user paths, which place the project's state directory.</param>
    /// <param name="targetRoot">The test's project root. It may already exist, but not be installed-into.</param>
    public void CopyInto(FakeSquadUserPaths userPaths, string targetRoot)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        SquadInstallCopy.Tree(_projectRoot, targetRoot);
        SquadInstallCopy.Tree(_homeDirectory, userPaths.HomeDirectory);

        string templateRoot = SquadInstallCopy.Canonical(_projectRoot);
        string copyRoot = SquadInstallCopy.Canonical(targetRoot);
        string from = ProjectStateDirectory(userPaths.HomeDirectory, templateRoot);
        string to = ProjectStateDirectory(userPaths.HomeDirectory, copyRoot);
        if (Directory.Exists(from) && !string.Equals(from, to, StringComparison.Ordinal))
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
        }

        // Only state is re-rooted: a deployed file's bytes are what its receipt digest names.
        SquadInstallCopy.ReRoot(userPaths.HomeDirectory, templateRoot, copyRoot);
    }

    private static string RecipeKey(
        string recipe,
        SquadTarget[] targets,
        IReadOnlyDictionary<SquadTarget, string> presetNames,
        (string Name, string Yaml)[] presets) =>
        string.Join(
            "/",
            "squad-preset-project",
            recipe,
            string.Join(",", targets.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal)),
            string.Join(",", presetNames.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")),
            string.Join(",", presets.Select(preset => preset.Name).Order(StringComparer.Ordinal)));

    private static Install Build(
        string key,
        SquadTarget[] targets,
        IReadOnlyDictionary<SquadTarget, string> presetNames,
        (string Name, string Yaml)[] presets)
    {
        TempDirectory? directory = new();
        try
        {
            string projectRoot = Path.Combine(directory.Path, "project");
            _ = Directory.CreateDirectory(projectRoot);
            FakeSquadUserPaths userPaths = new(Path.Combine(directory.Path, "user-data"));
            foreach ((string name, string yaml) in presets)
                WritePreset(userPaths, name, yaml);

            SquadStateStore stateStore = new(userPaths);
            using FakeSquadReleaseSource releaseSource = new();
            SquadLifecycleService service =
                SquadHostDefaults.CreateLifecycleService(releaseSource, new FakeSquadRenderer(), stateStore);

            SquadLifecycleResult result = service.InstallAsync(new SquadInstallRequest(
                    projectRoot,
                    SquadDeploymentScope.Project,
                    targets,
                    Version: "1.0.0",
                    ModelPresets: presetNames.Count == 0 ? null : new Dictionary<SquadTarget, string>(presetNames)))
                .GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"The prepared preset install failed: {string.Join("; ", result.Errors ?? [])}");
            }

            Install install = new(key, directory, projectRoot, userPaths.HomeDirectory);
            directory = null;
            return install;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    private static void WritePreset(ISquadUserPaths userPaths, string name, string yaml)
    {
        string presetsDirectory = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        _ = Directory.CreateDirectory(presetsDirectory);
        File.WriteAllText(Path.Combine(presetsDirectory, $"{name}.yml"), yaml);
    }

    private static string ProjectStateDirectory(string homeDirectory, string canonicalRoot) =>
        Path.Combine(
            homeDirectory,
            ".kyber-weave",
            "squad",
            "projects",
            SquadPhysicalRootIdentity.KeyOf(canonicalRoot));
}
