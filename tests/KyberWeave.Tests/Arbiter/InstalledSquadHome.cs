using KyberWeave.Application.Squad;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Configuration;

namespace KyberWeave.Tests.Arbiter;

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

    private readonly TempDirectory _directory;

    private InstalledSquadHome(TempDirectory directory, IReadOnlyList<SquadTarget> targets, SquadDeploymentScope scope)
    {
        _directory = directory;
        Targets = targets;
        Scope = scope;
    }

    /// <summary>The template directory, which callers copy from and never write to.</summary>
    internal string TemplatePath => _directory.Path;

    /// <summary>The targets this template installed.</summary>
    internal IReadOnlyList<SquadTarget> Targets { get; }

    /// <summary>The scope this template deployed into.</summary>
    internal SquadDeploymentScope Scope { get; }

    /// <summary>
    /// Installs <paramref name="targets"/> through the real Squad lifecycle, after
    /// <paramref name="seed"/> writes any operator content the install should merge with.
    /// </summary>
    /// <param name="targets">The harnesses to install, in one deployment.</param>
    /// <param name="scope">Whether the deployment is at the project root or the home.</param>
    /// <param name="seed">Writes operator-owned files into the empty root before the install.</param>
    /// <returns>The built template, owned by the caller.</returns>
    internal static InstalledSquadHome Build(
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope = SquadDeploymentScope.Global,
        Action<string>? seed = null)
    {
        TempDirectory directory = new();
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
                new FileUserHostConfigStore().WriteGlobalTargets(
                    directory.Path,
                    targets.Select(SquadTargetCatalog.GetToken).ToArray());
            }

            return new InstalledSquadHome(directory, targets, scope);
        }
        catch
        {
            directory.Dispose();
            throw;
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
        CopyTree(TemplatePath, destination, includeHostConfiguration);

        // The receipt records the root it was written for. The copy is a different root, so the
        // record is rewritten to name it rather than carrying a path outside the test's directory.
        string receiptPath = ResolveReceiptPath(destination);
        if (!File.Exists(receiptPath))
            return null;

        SquadStateStore store = Store(destination);
        SquadReceipt receipt = store.DeserializeReceipt(File.ReadAllText(receiptPath)) with
        {
            TargetRoot = destination
        };
        File.WriteAllText(receiptPath, store.SerializeReceipt(receipt));
        return receipt;
    }

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

    private static SquadStateStore Store(string root) =>
        new(new SquadHostDefaults.CliSquadUserPaths(root, root));

    private string ResolveReceiptPath(string root) => ResolveReceiptPath(root, Scope);

    private static string ResolveReceiptPath(string root, SquadDeploymentScope scope) =>
        Store(root).ResolveReceiptPath(root, scope);

    private static void CopyTree(string source, string destination, bool includeHostConfiguration)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            _ = Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (!includeHostConfiguration
                && string.Equals(
                    relative.Replace(Path.DirectorySeparatorChar, '/'),
                    HostConfigRelativePath,
                    StringComparison.Ordinal))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string CheckoutSourceRoot() =>
        new PhysicalSquadFileAccess().FindCheckoutSquadSourceRoot()
        ?? throw new InvalidOperationException(
            "The Squad source is not available from the checkout above the test binaries, so the class template cannot be built.");
}
