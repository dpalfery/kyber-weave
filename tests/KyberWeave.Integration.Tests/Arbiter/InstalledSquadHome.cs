using KyberWeave.Tests.Configuration;
using KyberWeave.Application.Squad;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Configuration;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Integration.Tests.Arbiter;

/// <summary>
/// One real Kyber-Squad deployment, built once and copied per test.
/// </summary>
/// <remarks>
/// A real install renders the whole bundle and then commits it through the deployment transaction,
/// which rewrites its journal once per file: about 1.5 s for one target. A test that only needs a
/// deployment to start from should not pay that per test. The class fixture builds this one, the
/// tests copy it into their own directory, and the copy is a directory copy (<see cref="File.Copy"/>,
/// which uses the platform's copy-on-write clone where the file system offers one). The template is
/// never written after it is built; each test owns and removes its copy.
/// </remarks>
public sealed class InstalledSquadHome : IDisposable
{
    /// <summary>The host configuration path, which a copy leaves to the test that owns the copy.</summary>
    private const string HostConfigRelativePath = ".kyber-weave/kyber-weave.yml";

    private InstalledSquadHome(string templatePath, IReadOnlyList<SquadTarget> targets, SquadDeploymentScope scope)
    {
        TemplatePath = templatePath;
        Targets = targets;
        Scope = scope;
    }

    /// <summary>The template directory, which callers copy from and never write to.</summary>
    internal string TemplatePath { get; }

    /// <summary>The targets this template installed.</summary>
    internal IReadOnlyList<SquadTarget> Targets { get; }

    /// <summary>The scope this template deployed into.</summary>
    internal SquadDeploymentScope Scope { get; }

    /// <summary>
    /// Gets the install of <paramref name="targets"/> at <paramref name="scope"/> that the whole run
    /// shares, building it through the real Squad lifecycle on the first request.
    /// </summary>
    /// <param name="targets">The harnesses to install, in one deployment.</param>
    /// <param name="scope">Whether the deployment is at the project root or the home.</param>
    /// <param name="seed">Writes operator-owned files into the empty root before the install.</param>
    /// <param name="recipe">Names a seeded variant, so two different seeds never share one install.</param>
    /// <returns>The shared template. It belongs to <see cref="PreparedSquadInstalls"/>, which removes it when the run ends.</returns>
    internal static InstalledSquadHome Build(
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope = SquadDeploymentScope.Global,
        Action<string>? seed = null,
        string recipe = "plain")
    {
        ArgumentNullException.ThrowIfNull(targets);
        string key = string.Join(
            "/",
            "squad-home",
            scope,
            string.Join(",", targets.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal)),
            recipe);
        return new InstalledSquadHome(
            PreparedSquadInstalls.Get(key, () => Create(targets, scope, seed)).TemplatePath,
            targets,
            scope);
    }

    /// <summary>The shared install itself, which owns the directory and removes it when the run ends.</summary>
    private sealed class Home(string key, TempDirectory directory) : PreparedSquadInstall(key, directory);

    private static Home Create(
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope,
        Action<string>? seed)
    {
        TempDirectory? directory = new();
        try
        {
            seed?.Invoke(directory.Path);

            SquadStateStore store = new(new SquadHostDefaults.CliSquadUserPaths(directory.Path, directory.Path));
            ISquadLifecycleService lifecycle = SquadHostDefaults.CreateLifecycleService(
                SquadReleaseSources.LocalCheckout(CheckoutSourceRoot()),
                SquadHostDefaults.ResolveRenderer(),
                store,
                globalRoots: new SquadGlobalRoots(_ => null, directory.Path));
            SquadLifecycleResult result = lifecycle.InstallAsync(new SquadInstallRequest(
                    directory.Path,
                    scope,
                    targets,
                    Version: "0.0.0",
                    Arbiter: new SquadArbiterWiring(true, ArbiterHookTimeout.SecondsFor(30_000))))
                .GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"The class template install failed: {string.Join("; ", result.Errors ?? [])}");
            }

            if (scope == SquadDeploymentScope.Global)
            {
                // The install writes the receipt; the user-level target ledger is the Arbiter install
                // command's own step, and the uninstall command reads it to unregister the target.
                RealUserHostConfigStore.Create().WriteGlobalTargets(
                    directory.Path,
                    targets.Select(SquadTargetCatalog.GetToken).ToArray());
            }

            Home home = new("squad-home", directory);
            directory = null;
            return home;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    /// <summary>
    /// Copies the template into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">An existing directory that receives the copy.</param>
    /// <param name="includeHostConfiguration">
    /// Whether the copy carries the host configuration and global target ledger. A test that lets the
    /// command write its own configuration passes false; a test that starts from the ledger passes true.
    /// </param>
    /// <returns>The copied receipt, rewritten to name <paramref name="destination"/>, or null.</returns>
    internal SquadReceipt? CopyInto(string destination, bool includeHostConfiguration = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        SquadInstallCopy.Tree(
            TemplatePath,
            destination,
            includeHostConfiguration
                ? null
                : relative => !string.Equals(
                    relative.Replace(Path.DirectorySeparatorChar, '/'),
                    HostConfigRelativePath,
                    StringComparison.Ordinal));

        // The receipt records the root it was written for. The copy is a different root, so the
        // record is rewritten to name it rather than carrying a path outside the test's directory.
        SquadInstallCopy.ReRoot(
            Path.Combine(destination, ".kyber-weave", "squad"),
            SquadInstallCopy.Canonical(TemplatePath),
            SquadInstallCopy.Canonical(destination));

        string receiptPath = ResolveReceiptPath(destination);
        if (!File.Exists(receiptPath))
            return null;

        SquadStateStore store = Store(destination);
        SquadReceipt receipt = store.DeserializeReceipt(File.ReadAllText(receiptPath));
        if (!string.Equals(receipt.TargetRoot, destination, StringComparison.Ordinal))
        {
            receipt = receipt with { TargetRoot = destination };
            File.WriteAllText(receiptPath, store.SerializeReceipt(receipt));
        }

        return receipt;
    }

    /// <remarks>
    /// The provider removes this install when the run ends. A test copies it and disposes the copy; it
    /// never disposes the shared install, which later tests still read.
    /// </remarks>
    public void Dispose()
    {
    }

    private static SquadStateStore Store(string root) =>
        new(new SquadHostDefaults.CliSquadUserPaths(root, root));

    private string ResolveReceiptPath(string root) => ResolveReceiptPath(root, Scope);

    private static string ResolveReceiptPath(string root, SquadDeploymentScope scope) =>
        Store(root).ResolveReceiptPath(root, scope);

    private static string CheckoutSourceRoot() =>
        new PhysicalSquadFileAccess().FindCheckoutSquadSourceRoot()
        ?? throw new InvalidOperationException(
            "The Squad source is not available from the checkout above the test binaries, so the class template cannot be built.");
}
