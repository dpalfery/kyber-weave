using KyberWeave.Testing.Squad;
using System.Security.Cryptography;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Tests.Corpus;
using KyberWeave.Application.Arbiter;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Pins the Copilot global-scope resource-link contract end to end through the real renderer,
/// the lifecycle service, the receipt, and <c>squad doctor --global</c>.
/// Unit twin: <c>SquadCopilotGlobalLinksUnitTests</c>.
/// </summary>
/// <remarks>
/// A global Copilot install has no workspace, so a relative agent link such as
/// <c>conductor/references/intake-path.md</c> has nothing to anchor to and the model falls
/// back to guessing a skill root. The rendered link therefore has to be absolute under the
/// resolved Copilot root, and the install, the receipt, and doctor must all see the same
/// bytes — otherwise doctor reports the managed conductor as an unmanaged collision.
/// </remarks>
public sealed class SquadHotshotLifecycleCopilotGlobalLinksTests : IDisposable
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ConductorPath = "agents/conductor.agent.md";

    private static readonly string[] ReferenceFiles =
    [
        "plan-path.md",
        "spec-path.md",
        "intake-path.md",
        "autonomy.md"
    ];

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    /// The installed conductor must link each declared reference by an absolute path under the
    /// resolved Copilot root, and the receipt digest must describe exactly the bytes on disk.
    /// </summary>
    [Fact]
    public async Task GlobalCopilotInstallWritesAbsoluteReferenceLinksAndReceiptDigestMatchesWrittenBytes()
    {
        GlobalInstall install = InstallGlobalCopilotAsync("links-install");

        string content = await File.ReadAllTextAsync(install.ConductorFile);
        foreach (string reference in ReferenceFiles)
        {
            string expectedLink = $"]({install.RootForwardSlashed}/agents/conductor/references/{reference})";
            Assert.True(
                content.Contains(expectedLink, StringComparison.Ordinal),
                $"Installed conductor does not contain the absolute link '{expectedLink}'.");
        }

        Assert.DoesNotContain("](conductor/references/", content, StringComparison.Ordinal);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            install.StateStore.ReadReceipt(Corpus.Root, SquadDeploymentScope.Global));
        SquadOwnedFile owned = Assert.Single(
            receipt.Files,
            file => string.Equals(file.RelativePath, ConductorPath, StringComparison.Ordinal)
                    && string.Equals(file.Target, "copilot", StringComparison.Ordinal));
        string writtenDigest = Convert.ToHexStringLower(
            SHA256.HashData(await File.ReadAllBytesAsync(install.ConductorFile)));
        Assert.Equal(writtenDigest, owned.Sha256);
    }

    /// <summary>
    /// Doctor re-renders the canonical source and compares it with the deployed bytes. When it
    /// renders without the global root the conductor differs from the installed one, so a
    /// managed file would be reported as an unmanaged collision.
    /// </summary>
    [Fact]
    public async Task DoctorGlobalScanAfterGlobalCopilotInstallReportsNoUnmanagedCollisionForConductor()
    {
        GlobalInstall install = InstallGlobalCopilotAsync("links-doctor");

        // Guard the precondition so a doctor pass cannot come from an install that wrote nothing.
        Assert.True(File.Exists(install.ConductorFile));
        string installed = await File.ReadAllTextAsync(install.ConductorFile);
        Assert.Contains(
            $"]({install.RootForwardSlashed}/agents/conductor/references/intake-path.md)",
            installed,
            StringComparison.Ordinal);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithMatchingArbiterProbe();
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            install.UserPaths,
            Corpus.Root,
            install.GlobalRoots,
            SquadTestRegistry.Create([new CopilotRenderer()]));

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = Corpus.Root,
                Global = true
            }));

        string normalizedOutput = string.Join(
            ' ',
            execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.True(
            normalizedOutput.Contains("Global unmanaged collisions: none", StringComparison.OrdinalIgnoreCase),
            $"Doctor reported a collision after a clean install. Output: {execution.Output}");
        Assert.DoesNotContain(ConductorPath, execution.Output, StringComparison.Ordinal);
        Assert.Equal(0, execution.Result);
    }

    private GlobalInstall InstallGlobalCopilotAsync(string name)
    {
        PreparedCopilotGlobal prepared = PreparedSquadInstalls.Get(RecipeKey, () => BuildTemplate(RecipeKey));
        string home = Path.Combine(_temp.Path, name + "-home");
        Directory.CreateDirectory(home);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, name + "-user"));
        SquadGlobalRoots globalRoots = new(_ => null, home);
        string root = globalRoots.ResolveGlobalRoot(SquadTarget.Copilot);
        prepared.CopyInto(home, root, userPaths);

        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        string conductorFile = Path.Combine(root, "agents", "conductor.agent.md");
        return new GlobalInstall(
            userPaths,
            globalRoots,
            stateStore,
            root.Replace('\\', '/').TrimEnd('/'),
            conductorFile);
    }

    private const string RecipeKey = "hotshot-copilot-global-links/install/Global/copilot/1.2.3";

    /// <summary>
    /// The one real global Copilot install the class starts from: real renderer, real corpus, so the
    /// deployed conductor carries the absolute links under its own root.
    /// </summary>
    private static PreparedCopilotGlobal BuildTemplate(string key)
    {
        TempDirectory? directory = new();
        try
        {
            string home = Path.Combine(directory.Path, "home");
            Directory.CreateDirectory(home);
            FakeSquadUserPaths userPaths = new(Path.Combine(directory.Path, "user"));
            SquadGlobalRoots globalRoots = new(_ => null, home);
            SquadStateStore stateStore = RealSquadState.Store(userPaths);
            using CorpusSquadReleaseSource releaseSource = new();
            SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(
                releaseSource,
                SquadHostDefaults.ResolveRenderer(),
                stateStore,
                globalRoots: globalRoots);

            SquadLifecycleResult result = service.InstallAsync(new SquadInstallRequest(
                    Corpus.Root,
                    SquadDeploymentScope.Global,
                    [SquadTarget.Copilot],
                    Version: "1.2.3"))
                .GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"The prepared Copilot global install failed: {string.Join("; ", result.Errors ?? [])}");
            }

            PreparedCopilotGlobal prepared = new(
                key,
                directory,
                home,
                userPaths.ApplicationDataDirectory,
                SquadInstallRecord.FromLifecycle(result));
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    /// <summary>
    /// The shared install, copied into a test's own home and user data. The copy re-roots the absolute
    /// links inside the deployed conductor and records their new digests in the copied receipt, so the copy
    /// is a faithful install at its new root rather than bytes naming the template's root.
    /// </summary>
    private sealed class PreparedCopilotGlobal(
        string key,
        TempDirectory directory,
        string home,
        string applicationData,
        SquadInstallRecord record) : PreparedSquadInstall(key, directory, record)
    {
        public void CopyInto(string destinationHome, string destinationTargetRoot, ISquadUserPaths userPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationHome);
            ArgumentNullException.ThrowIfNull(userPaths);

            SquadInstallCopy.Tree(home, destinationHome);
            SquadInstallCopy.Tree(applicationData, userPaths.ApplicationDataDirectory);

            string templateRoot = SquadInstallCopy.Canonical(home);
            string copyRoot = SquadInstallCopy.Canonical(destinationHome);
            SquadStateStore store = RealSquadState.Store(userPaths);
            SquadInstallCopy.ReRoot(
                Path.Combine(userPaths.ApplicationDataDirectory, ".kyber-weave", "squad"),
                templateRoot,
                copyRoot);
            _ = SquadInstallCopy.ReRootDeployed(
                destinationTargetRoot,
                store.ResolveReceiptPath(Corpus.Root, SquadDeploymentScope.Global),
                store,
                home,
                destinationHome);
        }
    }

    private sealed record GlobalInstall(
        ISquadUserPaths UserPaths,
        ISquadGlobalRootResolver GlobalRoots,
        SquadStateStore StateStore,
        string RootForwardSlashed,
        string ConductorFile);
}
