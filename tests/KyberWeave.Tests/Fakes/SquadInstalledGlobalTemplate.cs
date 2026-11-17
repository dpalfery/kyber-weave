using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// One real global install, built once for the whole run and copied into each test's own fake home.
/// </summary>
/// <remarks>
/// A global install renders and applies every entry file for its targets and fsyncs a receipt for
/// them, which is more than a test that only exercises the next update needs to pay. The install is
/// made here through the real lifecycle with the real renderer, once per target set for the run, and is
/// never written again. <see cref="CopyInto"/> copies the home state, the application data directory and
/// each target's global root with <see cref="SquadInstallCopy.Tree"/> and rewrites the template's own
/// root inside the copied home state, so a copy looks to the next command exactly like an install that
/// ran in the test's own home.
/// </remarks>
public sealed class SquadInstalledGlobalTemplate : IDisposable
{

    private readonly string _harnessRoot;
    private readonly string _home;
    private readonly string _applicationData;
    private readonly string _globalRoots;

    /// <summary>Gets the shared install for this target set, building it through the real lifecycle on first request.</summary>
    /// <param name="targets">The targets the template's global deployment holds.</param>
    public SquadInstalledGlobalTemplate(params SquadTarget[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Install install = PreparedSquadInstalls.Get(RecipeKey(targets), () => Build(targets));
        _harnessRoot = install.HarnessRoot;
        _home = install.Home;
        _applicationData = install.ApplicationData;
        _globalRoots = install.GlobalRoots;
    }

    private static string RecipeKey(SquadTarget[] targets) => string.Join(
        "/",
        "squad-global-template",
        string.Join(",", targets.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal)));

    /// <summary>The shared install itself, which owns the directory and removes it when the run ends.</summary>
    private sealed class Install(
        string key,
        TempDirectory directory,
        string harnessRoot,
        string home,
        string applicationData,
        string globalRoots) : PreparedSquadInstall(key, directory)
    {
        public string HarnessRoot { get; } = harnessRoot;

        public string Home { get; } = home;

        public string ApplicationData { get; } = applicationData;

        public string GlobalRoots { get; } = globalRoots;
    }

    private static Install Build(SquadTarget[] targets)
    {
        TempDirectory? directory = new();
        using SquadHomeHarness harness = new(Path.Combine(directory.Path, "harness"));
        try
        {
            string anchor = harness.MakeDirectory("anchor");
            SquadLifecycleResult result = harness.Lifecycle(SquadHostDefaults.ResolveRenderer())
                .InstallAsync(new SquadInstallRequest(
                    anchor,
                    SquadDeploymentScope.Global,
                    targets,
                    Version: "1.2.3"))
                .GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"The class global template install failed: {string.Join("; ", result.Errors ?? [])}");
            }

            Install install = new(
                RecipeKey(targets),
                directory,
                harness.Root,
                harness.Home,
                harness.ApplicationData,
                harness.GlobalRootsDirectory);
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

    /// <summary>Gives <paramref name="harness"/> this template's global deployment and home state.</summary>
    internal void CopyInto(SquadHomeHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        SquadInstallCopy.Tree(_home, harness.Home);
        SquadInstallCopy.Tree(_applicationData, harness.ApplicationData);
        SquadInstallCopy.Tree(_globalRoots, harness.GlobalRootsDirectory);

        SquadInstallCopy.ReRoot(
            Path.Combine(harness.Home, ".kyber-weave", "squad"),
            SquadInstallCopy.Canonical(_harnessRoot),
            SquadInstallCopy.Canonical(harness.Root));
    }
}

/// <summary>
/// The two global deployments <see cref="SquadDeployTimeGuidanceWiringTests"/> starts its converted
/// cases from: one Codex home, and one home holding Codex and Claude.
/// </summary>
public sealed class SquadGuidanceTemplates : IDisposable
{
    /// <summary>Builds both templates once, each through the real lifecycle.</summary>
    public SquadGuidanceTemplates()
    {
        try
        {
            Codex = new SquadInstalledGlobalTemplate(SquadTarget.Codex);
            CodexAndClaude = new SquadInstalledGlobalTemplate(SquadTarget.Codex, SquadTarget.Claude);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>A global home holding the Codex deployment.</summary>
    public SquadInstalledGlobalTemplate Codex { get; private set; } = null!;

    /// <summary>A global home holding the Codex and Claude deployments.</summary>
    public SquadInstalledGlobalTemplate CodexAndClaude { get; private set; } = null!;

    /// <remarks>
    /// The two templates belong to <see cref="PreparedSquadInstalls"/>, which removes them when the run
    /// ends. Disposing them here would delete an install that another class still reads.
    /// </remarks>
    public void Dispose()
    {
    }
}
