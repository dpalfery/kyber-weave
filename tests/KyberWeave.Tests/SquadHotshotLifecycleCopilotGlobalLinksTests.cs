using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Copilot global-scope resource-link contract end to end through the real renderer,
/// the lifecycle service, the receipt, and <c>squad doctor --global</c>.
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
    private const string ConductorPath = "agents/conductor.agent.md";

    private static readonly string[] ReferenceFiles =
    [
        "plan-path.md",
        "spec-path.md",
        "intake-path.md",
        "execution-and-review.md",
    ];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The installed conductor must link each declared reference by an absolute path under the
    /// resolved Copilot root, and the receipt digest must describe exactly the bytes on disk.
    /// </summary>
    [Fact]
    public async Task GlobalCopilotInstallWritesAbsoluteReferenceLinksAndReceiptDigestMatchesWrittenBytes()
    {
        GlobalInstall install = await InstallGlobalCopilotAsync("links-install");

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
            install.StateStore.ReadReceipt(KyberWeaveTestPaths.ToolRoot, SquadDeploymentScope.Global));
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
        GlobalInstall install = await InstallGlobalCopilotAsync("links-doctor");

        // Guard the precondition so a doctor pass cannot come from an install that wrote nothing.
        Assert.True(File.Exists(install.ConductorFile));
        string installed = await File.ReadAllTextAsync(install.ConductorFile);
        Assert.Contains(
            $"]({install.RootForwardSlashed}/agents/conductor/references/intake-path.md)",
            installed,
            StringComparison.Ordinal);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = new(
            executor,
            install.UserPaths,
            workingDirectory: KyberWeaveTestPaths.ToolRoot,
            globalRoots: install.GlobalRoots);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = KyberWeaveTestPaths.ToolRoot,
                Global = true,
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

    private async Task<GlobalInstall> InstallGlobalCopilotAsync(string name)
    {
        string home = Path.Combine(_temp.Path, name + "-home");
        Directory.CreateDirectory(home);
        FakeUserPaths userPaths = new(Path.Combine(_temp.Path, name + "-user"));
        SquadGlobalRoots globalRoots = new(_ => null, home);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        SquadLifecycleService service = new(
            releaseSource,
            SquadCommandComposition.ResolveRenderer(),
            stateStore,
            globalRoots: globalRoots);

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: KyberWeaveTestPaths.ToolRoot,
            Scope: SquadDeploymentScope.Global,
            Targets: [SquadTarget.Copilot],
            Version: "1.2.3"));
        Assert.True(result.Success);

        string root = globalRoots.ResolveGlobalRoot(SquadTarget.Copilot);
        string conductorFile = Path.Combine(root, "agents", "conductor.agent.md");
        return new GlobalInstall(
            userPaths,
            globalRoots,
            stateStore,
            root.Replace('\\', '/').TrimEnd('/'),
            conductorFile);
    }

    private sealed record GlobalInstall(
        ISquadUserPaths UserPaths,
        ISquadGlobalRootResolver GlobalRoots,
        SquadStateStore StateStore,
        string RootForwardSlashed,
        string ConductorFile);

    private sealed class FakeUserPaths(string appDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = appDataDirectory;

        public string HomeDirectory => Path.Combine(ApplicationDataDirectory, "home");
    }
}
