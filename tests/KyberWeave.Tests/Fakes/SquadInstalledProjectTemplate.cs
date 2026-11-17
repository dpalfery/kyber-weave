using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// One real project install, built once for the whole run and copied into each test's own project root
/// and fake home.
/// </summary>
/// <remarks>
/// A real install journals, stages, backs up and applies every rendered file and fsyncs the receipt, so
/// arranging a deployment cost more than most tests assert. <see cref="SquadLifecycleTests"/> only needs a
/// project that is already installed as the starting point for an install, update or uninstall. The
/// install is made here through the real lifecycle with the class's fakes, once for the run, and is never
/// written again. <see cref="CopyInto"/> copies the project tree and the project's home state directory
/// with <see cref="SquadInstallCopy.Tree"/>, keys the state directory to the copy's own physical root,
/// and rewrites the receipt's recorded root, so every test works on its own isolated files.
/// </remarks>
public sealed class SquadInstalledProjectTemplate : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _homeDirectory;

    /// <summary>
    /// Gets the shared project deployment of the Codex target, building it through the real lifecycle
    /// with the class's fakes on the first request.
    /// </summary>
    public SquadInstalledProjectTemplate()
    {
        Install install = PreparedSquadInstalls.Get(RecipeKey, Build);
        _projectRoot = install.ProjectRoot;
        _homeDirectory = install.HomeDirectory;
    }

    private const string RecipeKey = "squad-project-template/codex";

    /// <summary>The shared install itself, which owns the directory and removes it when the run ends.</summary>
    private sealed class Install(string key, TempDirectory directory, string projectRoot, string homeDirectory)
        : PreparedSquadInstall(key, directory)
    {
        public string ProjectRoot { get; } = projectRoot;

        public string HomeDirectory { get; } = homeDirectory;
    }

    private static Install Build()
    {
        TempDirectory? directory = new();
        try
        {
            string projectRoot = Path.Combine(directory.Path, "project");
            _ = Directory.CreateDirectory(projectRoot);

            FakeSquadUserPaths userPaths = new(Path.Combine(directory.Path, "user-data"));
            SquadStateStore stateStore = new(userPaths);
            using FakeSquadReleaseSource releaseSource = new();
            SquadLifecycleService service =
                SquadHostDefaults.CreateLifecycleService(releaseSource, new FakeSquadRenderer(), stateStore);

            SquadLifecycleResult result = service.InstallAsync(new SquadInstallRequest(
                    projectRoot,
                    SquadDeploymentScope.Project,
                    [SquadTarget.Codex],
                    Version: "1.2.3"))
                .GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"The class template install failed: {string.Join("; ", result.Errors ?? [])}");
            }

            Install install = new(RecipeKey, directory, projectRoot, userPaths.HomeDirectory);
            directory = null;
            return install;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    /// <remarks>
    /// The install belongs to <see cref="PreparedSquadInstalls"/>, which removes it when the run ends.
    /// A test copies it through <see cref="CopyInto"/> and disposes the copy.
    /// </remarks>
    public void Dispose()
    {
    }

    /// <summary>
    /// Gives <paramref name="targetRoot"/> this template's deployment and its home state, as if the
    /// test had installed it there itself.
    /// </summary>
    /// <param name="userPaths">The test's fake user paths, which place the project's state directory.</param>
    /// <param name="targetRoot">The test's project root. It may already be empty, but not installed-into.</param>
    public void CopyInto(FakeSquadUserPaths userPaths, string targetRoot)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        SquadInstallCopy.Tree(_projectRoot, targetRoot);

        string copyRoot = SquadInstallCopy.Canonical(targetRoot);
        string copyState = ProjectStateDirectory(userPaths.HomeDirectory, copyRoot);
        SquadInstallCopy.Tree(ProjectStateDirectory(_homeDirectory, SquadInstallCopy.Canonical(_projectRoot)), copyState);
        SquadInstallCopy.ReRoot(copyState, SquadInstallCopy.Canonical(_projectRoot), copyRoot);
    }

    private static string ProjectStateDirectory(string homeDirectory, string canonicalRoot) =>
        Path.Combine(
            homeDirectory,
            ".kyber-weave",
            "squad",
            "projects",
            SquadPhysicalRootIdentity.KeyOf(canonicalRoot));
}
