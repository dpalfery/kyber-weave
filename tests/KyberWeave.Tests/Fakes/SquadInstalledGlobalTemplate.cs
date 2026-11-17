using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// One real global install, built once for a test class and copied into each test's own fake home.
/// </summary>
/// <remarks>
/// A global install renders and applies every entry file for its targets and fsyncs a receipt for
/// them, which is more than a test that only exercises the next update needs to pay. The template is
/// installed here through the real lifecycle with the real renderer and is never written again.
/// <see cref="CopyInto"/> copies the home state, the application data directory and each target's
/// global root with <see cref="File.Copy"/> (which uses the platform's copy-on-write clone where the
/// file system offers one) and rewrites the template's own root inside the copied home state, so a copy
/// looks to the next command exactly like an install that ran in the test's own home.
/// </remarks>
public sealed class SquadInstalledGlobalTemplate : IDisposable
{
    private static readonly SquadFileSystemPathSemantics Semantics = SquadFileSystemPathSemantics.Instance;

    private readonly TempDirectory _directory = new();
    private readonly string _harnessRoot;
    private readonly string _home;
    private readonly string _applicationData;
    private readonly string _globalRoots;

    /// <summary>Installs the template once for the declaring class, through the real lifecycle.</summary>
    /// <param name="targets">The targets the template's global deployment holds.</param>
    public SquadInstalledGlobalTemplate(params SquadTarget[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        using SquadHomeHarness harness = new(Path.Combine(_directory.Path, "harness"));
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
        }
        catch
        {
            _directory.Dispose();
            throw;
        }

        _harnessRoot = harness.Root;
        _home = harness.Home;
        _applicationData = harness.ApplicationData;
        _globalRoots = harness.GlobalRootsDirectory;
    }

    /// <summary>Gives <paramref name="harness"/> this template's global deployment and home state.</summary>
    internal void CopyInto(SquadHomeHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        CopyTree(_home, harness.Home);
        CopyTree(_applicationData, harness.ApplicationData);
        CopyTree(_globalRoots, harness.GlobalRootsDirectory);

        string from = Canonical(_harnessRoot);
        string to = Canonical(harness.Root);
        if (!string.Equals(from, to, StringComparison.Ordinal))
            RewriteRecordedRoot(Path.Combine(harness.Home, ".kyber-weave", "squad"), from, to);
    }

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

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
    /// Rewrites the template's own root in the copied state files. Only the state directory is
    /// touched: a deployed file's bytes are what its receipt digest names, so rewriting one would read
    /// as a hand edit to the next update.
    /// </summary>
    private static void RewriteRecordedRoot(string stateDirectory, string from, string to)
    {
        if (!Directory.Exists(stateDirectory))
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

    /// <inheritdoc />
    public void Dispose()
    {
        Codex?.Dispose();
        CodexAndClaude?.Dispose();
    }
}
