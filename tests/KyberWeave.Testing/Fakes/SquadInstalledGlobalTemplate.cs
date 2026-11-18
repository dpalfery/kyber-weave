using System.Text;
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
/// made here through the real lifecycle with the real renderer, once per scenario for the run, and is
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
        : this(targets, homeGuidance: null, blockHomeGuidance: false)
    {
    }

    private SquadInstalledGlobalTemplate(SquadTarget[] targets, string? homeGuidance, bool blockHomeGuidance)
    {
        ArgumentNullException.ThrowIfNull(targets);
        string key = RecipeKey(targets, homeGuidance, blockHomeGuidance);
        Install install = PreparedSquadInstalls.Get(
            key,
            () => Build(key, targets, homeGuidance, blockHomeGuidance));
        _harnessRoot = install.HarnessRoot;
        _home = install.Home;
        _applicationData = install.ApplicationData;
        _globalRoots = install.GlobalRoots;
        Record = install.Record;
        HomeGuidanceExistedBeforeInstall = install.HomeGuidanceExistedBeforeInstall;
    }

    /// <summary>
    /// Gets the shared Codex install whose home held <paramref name="text"/> as its response guidance
    /// when the install ran.
    /// </summary>
    /// <param name="text">The operator's guidance, which the install injects into its entry files.</param>
    /// <returns>The one prepared install for that text.</returns>
    public static SquadInstalledGlobalTemplate WithHomeGuidance(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new SquadInstalledGlobalTemplate([SquadTarget.Codex], text, blockHomeGuidance: false);
    }

    /// <summary>
    /// Gets the shared Codex install whose home held a directory where its response guidance file
    /// belongs, so reading the guidance fails.
    /// </summary>
    /// <returns>The one prepared install for that home state.</returns>
    public static SquadInstalledGlobalTemplate WithUnreadableHomeGuidance() =>
        new([SquadTarget.Codex], homeGuidance: null, blockHomeGuidance: true);

    /// <summary>What the install returned: its exit code and the warnings it reported.</summary>
    public SquadInstallRecord Record { get; }

    /// <summary>Whether the home response guidance file existed before the install ran.</summary>
    public bool HomeGuidanceExistedBeforeInstall { get; }

    /// <summary>The template's fake home directory.</summary>
    public string Home => _home;

    /// <summary>The template's global root for <paramref name="target"/>.</summary>
    /// <param name="target">The target whose deployed files live under that root.</param>
    /// <returns>The root the install wrote that target's entry files to.</returns>
    public string GlobalRoot(SquadTarget target) =>
        Path.Combine(_globalRoots, SquadTargetCatalog.GetToken(target));

    private static string RecipeKey(SquadTarget[] targets, string? homeGuidance, bool blockHomeGuidance) =>
        SquadInstallRecipe.Key(
            "squad-global-template",
            SquadDeploymentScope.Global,
            targets,
            ("home-guidance", homeGuidance),
            ("home-guidance-blocked", blockHomeGuidance ? "true" : null));

    /// <summary>The shared install itself, which owns the directory and removes it when the run ends.</summary>
    private sealed class Install(
        string key,
        TempDirectory directory,
        string harnessRoot,
        string home,
        string applicationData,
        string globalRoots,
        bool homeGuidanceExistedBeforeInstall,
        SquadInstallRecord record) : PreparedSquadInstall(key, directory, record)
    {
        public string HarnessRoot { get; } = harnessRoot;

        public string Home { get; } = home;

        public string ApplicationData { get; } = applicationData;

        public string GlobalRoots { get; } = globalRoots;

        public bool HomeGuidanceExistedBeforeInstall { get; } = homeGuidanceExistedBeforeInstall;
    }

    private static Install Build(
        string key,
        SquadTarget[] targets,
        string? homeGuidance,
        bool blockHomeGuidance)
    {
        TempDirectory? directory = new();
        using SquadHomeHarness harness = new(Path.Combine(directory.Path, "harness"));
        try
        {
            string guidancePath = GuidancePath(harness);
            Directory.CreateDirectory(Path.GetDirectoryName(guidancePath)!);
            if (blockHomeGuidance)
            {
                Directory.CreateDirectory(guidancePath);
            }
            else if (homeGuidance is not null)
            {
                File.WriteAllText(guidancePath, homeGuidance, Encoding.UTF8);
            }

            bool homeGuidanceExisted = File.Exists(guidancePath);

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
                key,
                directory,
                harness.Root,
                harness.Home,
                harness.ApplicationData,
                harness.GlobalRootsDirectory,
                homeGuidanceExisted,
                SquadInstallRecord.FromLifecycle(result, exitCode: result.Success ? 0 : 1));
            directory = null;
            return install;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    private static string GuidancePath(SquadHomeHarness harness) =>
        Path.Combine(harness.Home, ".kyber-weave", "response-guidance.md");

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
/// One real project-scope install of every target under a home response guidance file, built once for
/// the run and read by the test that asserts the guidance each entry file received.
/// </summary>
/// <remarks>
/// A project install writes every target's entry files beneath the project root. A test that asserts
/// only what those files hold reads the prepared project instead of installing again; the install is
/// made through the real renderer, once per guidance text for the run, and is never written afterwards.
/// </remarks>
public sealed class SquadInstalledProjectGuidanceTemplate : IDisposable
{
    private readonly string _projectRoot;

    /// <summary>Gets the shared project install for <paramref name="homeGuidance"/>.</summary>
    /// <param name="homeGuidance">The operator's guidance, which the install injects into every entry file.</param>
    public SquadInstalledProjectGuidanceTemplate(string homeGuidance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeGuidance);

        string key = SquadInstallRecipe.Key(
            "squad-project-guidance",
            SquadDeploymentScope.Project,
            SquadTargetCatalog.All,
            ("home-guidance", homeGuidance));
        Install install = PreparedSquadInstalls.Get(key, () => Build(key, homeGuidance));
        _projectRoot = install.ProjectRoot;
        Record = install.Record;
    }

    /// <summary>The prepared project root, which holds every target's deployed entry files.</summary>
    public string ProjectRoot => _projectRoot;

    /// <summary>What the install returned: its exit code and the warnings it reported.</summary>
    public SquadInstallRecord Record { get; }

    /// <remarks>The install belongs to <see cref="PreparedSquadInstalls"/>, which removes it when the run ends.</remarks>
    public void Dispose()
    {
    }

    private sealed class Install(string key, TempDirectory directory, string projectRoot, SquadInstallRecord record)
        : PreparedSquadInstall(key, directory, record)
    {
        public string ProjectRoot { get; } = projectRoot;
    }

    private static Install Build(string key, string homeGuidance)
    {
        TempDirectory? directory = new();
        using SquadHomeHarness harness = new(Path.Combine(directory.Path, "harness"));
        try
        {
            string guidancePath = Path.Combine(harness.Home, ".kyber-weave", "response-guidance.md");
            Directory.CreateDirectory(Path.GetDirectoryName(guidancePath)!);
            File.WriteAllText(guidancePath, homeGuidance, Encoding.UTF8);

            string projectRoot = harness.MakeDirectory("project");
            SquadLifecycleResult result = harness.Lifecycle(SquadHostDefaults.ResolveRenderer())
                .InstallAsync(new SquadInstallRequest(
                    projectRoot,
                    SquadDeploymentScope.Project,
                    SquadTargetCatalog.All,
                    Version: "1.2.3"))
                .GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"The class project template install failed: {string.Join("; ", result.Errors ?? [])}");
            }

            Install install = new(
                key,
                directory,
                projectRoot,
                SquadInstallRecord.FromLifecycle(result, exitCode: result.Success ? 0 : 1));
            directory = null;
            return install;
        }
        finally
        {
            directory?.Dispose();
        }
    }
}

/// <summary>
/// The two global deployments <see cref="SquadDeployTimeGuidanceWiringTests"/> starts its update cases
/// from, and the empty home its home-guidance-creation case reads: one Codex home, and one home holding
/// Codex and Claude.
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
