using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// One global deployment and one project deployment, installed once for the test class and copied
/// into each test's own fake home.
/// </summary>
/// <remarks>
/// Almost every test in <see cref="SquadGlobalHomeCliTests"/> arranges a home that already holds a
/// deployment, and a real install costs about 150–250 ms however few files it writes: the deployment
/// transaction journals, stages, backs up and applies each file and fsyncs the receipt, and the
/// render re-reads the release. Fifteen tests arrange a fleet of four deployments, so the arrangement
/// was most of the class's running time.
/// <para>
/// The templates are built here through the real Squad lifecycle, once, and never written again.
/// <see cref="CopyGlobalInto"/> and <see cref="CopyProjectInto"/> copy one into a test's harness with
/// <see cref="File.Copy"/> (which uses the platform's copy-on-write clone where the file system offers
/// one) and re-root the recorded absolute paths: the global receipt records its home, a project
/// receipt and its registry entry record the project root, and the home keys a project's state
/// directory by the hash of that root, so the copy moves the state directory to the copy's key.
/// </para>
/// A test that exercises install itself still runs the real command: see
/// <c>FreshGlobalInstallAfterUninstallOfMigratedStateWritesNewHomeState</c>.
/// </remarks>
public sealed class SquadHomeStateTemplate : IDisposable
{
    private static readonly SquadFileSystemPathSemantics Semantics = SquadFileSystemPathSemantics.Instance;

    private readonly TempDirectory _directory;
    private readonly string _globalHarness;
    private readonly string _projectHarness;
    private readonly string _projectRoot;
    private readonly IReadOnlyList<string> _projectTargets;
    private readonly DateTimeOffset _projectInstalledAt;

    /// <summary>Installs both templates through the real lifecycle, once for the class that declares this fixture.</summary>
    public SquadHomeStateTemplate()
    {
        _directory = new TempDirectory();
        try
        {
            FakeSquadRenderer renderer = FakeSquadRenderer.ScriptedAgents("architect");

            _globalHarness = BuildGlobalTemplate(renderer);
            _projectHarness = Path.Combine(_directory.Path, "project-template");
            _projectRoot = Path.Combine(_projectHarness, "project");
            _ = Directory.CreateDirectory(_projectRoot);
            Install(_projectHarness, renderer, _projectRoot, SquadDeploymentScope.Project);

            SquadReceipt receipt = ReadReceipt(_projectHarness, _projectRoot, SquadDeploymentScope.Project);
            _projectTargets = [.. receipt.Files.Select(file => file.Target).Distinct(StringComparer.Ordinal)];
            _projectInstalledAt = receipt.InstalledAtUtc;
        }
        catch
        {
            _directory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies the global deployment into <paramref name="harness"/>: the home state, the home-owned
    /// response guidance, the canonical profile snapshot, and the files under each target's global root.
    /// </summary>
    internal void CopyGlobalInto(SquadHomeHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        CopyTree(Path.Combine(_globalHarness, "home"), harness.Home);
        CopyTree(Path.Combine(_globalHarness, "app-data"), harness.ApplicationData);
        CopyTree(Path.Combine(_globalHarness, "global-roots"), harness.GlobalRootsDirectory);
        ReRoot(
            [harness.Home, harness.GlobalRootsDirectory],
            Canonical(Path.Combine(_globalHarness, "home")),
            Canonical(harness.Home));
    }

    /// <summary>
    /// Installs <paramref name="name"/> as a project holding this template's deployment, and registers
    /// it the way the install path registers a project.
    /// </summary>
    internal Project CopyProjectInto(SquadHomeHarness harness, string name)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string root = Path.Combine(harness.Root, name);
        CopyTree(_projectRoot, root);

        string stateDirectory = ProjectStateDirectory(harness.Home, Canonical(root));
        CopyTree(ProjectStateDirectory(Path.Combine(_projectHarness, "home"), Canonical(_projectRoot)), stateDirectory);
        ReRoot([root, stateDirectory], Canonical(_projectRoot), Canonical(root));

        harness.Store.RegisterProject(root, _projectTargets, _projectInstalledAt);

        string physical = SquadPhysicalRootIdentities.Resolve(root, Semantics).PhysicalPath;
        return new Project(root, physical, stateDirectory);
    }

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

    private string BuildGlobalTemplate(ISquadRenderer renderer)
    {
        string root = Path.Combine(_directory.Path, "global-template");
        string anchor = Path.Combine(root, "anchor");
        _ = Directory.CreateDirectory(anchor);
        Install(root, renderer, anchor, SquadDeploymentScope.Global);
        return root;
    }

    private static void Install(
        string harnessRoot,
        ISquadRenderer renderer,
        string targetRoot,
        SquadDeploymentScope scope)
    {
        using SquadHomeHarness harness = new(harnessRoot);
        SquadLifecycleResult result = harness.Lifecycle(renderer).InstallAsync(new SquadInstallRequest(
                targetRoot,
                scope,
                [SquadTarget.Codex],
                Version: "1.2.3"))
            .GetAwaiter().GetResult();
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"The class template install failed: {string.Join("; ", result.Errors ?? [])}");
        }
    }

    private static SquadReceipt ReadReceipt(string harnessRoot, string root, SquadDeploymentScope scope)
    {
        using SquadHomeHarness harness = new(harnessRoot);
        return harness.Store.ReadReceipt(root, scope)
               ?? throw new InvalidOperationException("The class template install left no receipt.");
    }

    private static string ProjectStateDirectory(string homeDirectory, string canonicalRoot) =>
        Path.Combine(
            homeDirectory,
            ".kyber-weave",
            "squad",
            "projects",
            SquadPhysicalRootIdentities.KeyOf(canonicalRoot));

    private static string Canonical(string path) => SquadPhysicalRootIdentities.CanonicalizeAllowingMissing(path, Semantics);

    private static void CopyTree(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            _ = Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ReRoot(IReadOnlyList<string> roots, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
            return;

        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                if (!text.Contains(from, StringComparison.Ordinal))
                    continue;

                File.WriteAllText(file, text.Replace(from, to, StringComparison.Ordinal), Encoding.UTF8);
            }
        }
    }

    /// <summary>One installed project: its root, its canonical physical path, and its home state directory.</summary>
    internal sealed record Project(string Root, string Physical, string StateDirectory)
    {
        /// <summary>Where the project's lock was written.</summary>
        internal string LockPath => Path.Combine(StateDirectory, "squad.lock.yml");

        /// <summary>Where the project's receipt was written.</summary>
        internal string ReceiptPath => Path.Combine(StateDirectory, "squad.receipt.json");
    }
}
