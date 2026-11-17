using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// One real project install, built once for a test class and copied into each test's own project
/// root and fake home.
/// </summary>
/// <remarks>
/// A real install journals, stages, backs up and applies every rendered file and fsyncs the receipt,
/// so arranging a deployment cost more than most tests assert. <see cref="SquadLifecycleTests"/> only
/// needs a project that is already installed as the starting point for an install, update or
/// uninstall. The template is installed here through the real lifecycle with the class's fakes and is
/// never written again. <see cref="CopyInto"/> copies the project tree and the project's home state
/// directory with <see cref="File.Copy"/> (which uses the platform's copy-on-write clone where the
/// file system offers one), keys the state directory to the copy's own physical root, and rewrites the
/// receipt's recorded root, so every test works on its own isolated files.
/// </remarks>
public sealed class SquadInstalledProjectTemplate : IDisposable
{
    private static readonly SquadFileSystemPathSemantics Semantics = SquadFileSystemPathSemantics.Instance;

    private readonly TempDirectory _directory = new();
    private readonly string _projectRoot;
    private readonly string _homeDirectory;

    /// <summary>
    /// Installs one project deployment of the Codex target once for the declaring class, with the
    /// class's fakes.
    /// </summary>
    public SquadInstalledProjectTemplate()
    {
        _projectRoot = Path.Combine(_directory.Path, "project");
        _ = Directory.CreateDirectory(_projectRoot);

        FakeSquadUserPaths userPaths = new(Path.Combine(_directory.Path, "user-data"));
        _homeDirectory = userPaths.HomeDirectory;
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        SquadLifecycleService service =
            SquadHostDefaults.CreateLifecycleService(releaseSource, new FakeSquadRenderer(), stateStore);

        SquadLifecycleResult result = service.InstallAsync(new SquadInstallRequest(
                _projectRoot,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.3"))
            .GetAwaiter().GetResult();
        if (!result.Success)
        {
            _directory.Dispose();
            throw new InvalidOperationException(
                $"The class template install failed: {string.Join("; ", result.Errors ?? [])}");
        }
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

        CopyTree(_projectRoot, targetRoot);

        string copyRoot = Canonical(targetRoot);
        string copyState = ProjectStateDirectory(userPaths.HomeDirectory, copyRoot);
        CopyTree(ProjectStateDirectory(_homeDirectory, Canonical(_projectRoot)), copyState);
        RewriteRecordedRoot(copyState, Canonical(_projectRoot), copyRoot);
    }

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

    private static string ProjectStateDirectory(string homeDirectory, string canonicalRoot) =>
        Path.Combine(
            homeDirectory,
            ".kyber-weave",
            "squad",
            "projects",
            SquadPhysicalRootIdentity.KeyOf(canonicalRoot));

    private static string Canonical(string path) =>
        SquadPhysicalRootIdentity.CanonicalizeAllowingMissing(path, Semantics);

    private static void CopyTree(string source, string destination)
    {
        _ = Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            _ = Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Rewrites the template's root in the copied state files. Only the state directory is touched:
    /// a deployed file's bytes are what its receipt digest names, so rewriting one would read as a
    /// hand edit to the next update.
    /// </summary>
    private static void RewriteRecordedRoot(string stateDirectory, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
            return;

        foreach (string file in Directory.EnumerateFiles(stateDirectory, "*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains(from, StringComparison.Ordinal))
                continue;

            File.WriteAllText(file, text.Replace(from, to, StringComparison.Ordinal));
        }
    }
}
