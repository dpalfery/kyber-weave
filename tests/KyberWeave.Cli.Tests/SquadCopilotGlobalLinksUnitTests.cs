using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Fakes;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit twin of <c>SquadHotshotLifecycleCopilotGlobalLinksTests</c> and
/// <c>SquadHotshotLifecycleCopilotGlobalUpdateLinksTests</c>: a global Copilot install renders the conductor's
/// reference links as absolute paths under the resolved Copilot root, the receipt digest equals the written bytes,
/// doctor reports no collision, and an update re-resolves the same global roots. The shipped source and the real
/// Copilot renderer run over the in-memory file system.
/// </summary>
public sealed class SquadCopilotGlobalLinksUnitTests
{
    private const string ConductorPath = "agents/conductor.agent.md";

    private static readonly string[] ReferenceFiles = ["plan-path.md", "spec-path.md", "intake-path.md", "autonomy.md"];

    private readonly InMemorySquadState _state = new();
    private readonly string _repository = Path.GetFullPath("/kw/work");
    private readonly SquadGlobalRoots _roots;

    public SquadCopilotGlobalLinksUnitTests()
    {
        _roots = new SquadGlobalRoots(_ => null, _state.UserPaths.HomeDirectory);
        _ = CanonicalSquadTree.CopyInto(_state.Files, Path.Combine(_repository, "products", "kyber-squad"));
        _state.Files.WriteAllText(Path.Combine(_repository, "KyberWeave.sln"), string.Empty);
    }

    /// <summary>Install links each reference by an absolute path under the Copilot root, and the receipt digest matches the file.</summary>
    [Fact]
    public async Task GlobalInstallWritesAbsoluteReferenceLinksAndReceiptDigestMatchesWrittenBytes()
    {
        await InstallAsync();

        string content = _state.Files.ReadAllText(ConductorFile());
        AssertAbsoluteLinks(content);
        AssertReceiptDigestMatchesFile();
    }

    /// <summary>Doctor re-renders with the global root, so the managed conductor is not reported as a collision.</summary>
    [Fact]
    public async Task DoctorGlobalScanAfterInstallReportsNoUnmanagedCollisionForConductor()
    {
        await InstallAsync();
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithMatchingArbiterProbe();
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            workingDirectory: _repository,
            globalRoots: _roots,
            renderer: Renderer());

        CommandExecution execution = CaptureDoctor(command);

        string output = string.Join(' ', execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.True(output.Contains("Global unmanaged collisions: none", StringComparison.OrdinalIgnoreCase), output);
        Assert.DoesNotContain(ConductorPath, execution.Output, StringComparison.Ordinal);
        Assert.Equal(0, execution.ExitCode);
    }

    /// <summary>An update over a drifted, unanchored conductor re-renders absolute links with the install's global roots.</summary>
    [Fact]
    public async Task GlobalUpdateRewritesAbsoluteReferenceLinksWithTheSameRootsAsInstall()
    {
        await InstallAsync();
        string conductor = ConductorFile();
        string older = Regex.Replace(
            _state.Files.ReadAllText(conductor),
            @"\]\([^)]*/agents/conductor/references/",
            "](conductor/references/");
        Assert.Contains("](conductor/references/", older, StringComparison.Ordinal);
        _state.Files.WriteAllText(conductor, older);
        string olderDigest = Convert.ToHexStringLower(SHA256.HashData(_state.Files.ReadAllBytes(conductor)));
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(_repository, SquadDeploymentScope.Global));
        SquadReceipt drifted = receipt with
        {
            Files = [.. receipt.Files.Select(file => IsConductor(file) ? file with { Sha256 = olderDigest } : file)],
        };
        _state.Files.WriteAllText(
            _state.Store.ResolveReceiptPath(_repository, SquadDeploymentScope.Global),
            _state.Store.SerializeReceipt(drifted));

        SquadLifecycleResult update = await Lifecycle().UpdateAsync(
            new SquadUpdateRequest(_repository, SquadDeploymentScope.Global, Version: "1.2.3"));

        Assert.True(update.Success, string.Join("; ", update.Errors ?? []));
        AssertAbsoluteLinks(_state.Files.ReadAllText(conductor));
        AssertReceiptDigestMatchesFile();
    }

    private async Task InstallAsync()
    {
        SquadLifecycleResult install = await Lifecycle().InstallAsync(
            new SquadInstallRequest(_repository, SquadDeploymentScope.Global, [SquadTarget.Copilot], Version: "1.2.3"));
        Assert.True(install.Success, string.Join("; ", install.Errors ?? []));
        Assert.True(_state.Files.FileExists(ConductorFile()));
    }

    private SquadLifecycleService Lifecycle() => SquadTestCommands.InMemoryLifecycle(
        _state,
        FakeSquadReleaseSource.FromDirectory(Path.Combine(_repository, "products", "kyber-squad"), _state.Files),
        Renderer(),
        _roots);

    private SquadRendererRegistry Renderer() =>
        new([new CopilotRenderer()], new InMemorySquadServices(_state.Files).RenderInputReader());

    private string CopilotRoot() => _roots.ResolveGlobalRoot(SquadTarget.Copilot);

    private string ConductorFile() => Path.Combine(CopilotRoot(), "agents", "conductor.agent.md");

    private void AssertAbsoluteLinks(string content)
    {
        string root = CopilotRoot().Replace('\\', '/').TrimEnd('/');
        foreach (string reference in ReferenceFiles)
        {
            Assert.Contains($"]({root}/agents/conductor/references/{reference})", content, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("](conductor/references/", content, StringComparison.Ordinal);
    }

    private void AssertReceiptDigestMatchesFile()
    {
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(_repository, SquadDeploymentScope.Global));
        SquadOwnedFile owned = Assert.Single(receipt.Files, IsConductor);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_state.Files.ReadAllBytes(ConductorFile()))), owned.Sha256);
    }

    private static bool IsConductor(SquadOwnedFile file) =>
        string.Equals(file.RelativePath, ConductorPath, StringComparison.Ordinal)
        && string.Equals(file.Target, "copilot", StringComparison.Ordinal);

    private static CommandExecution CaptureDoctor(SquadDoctorCommand command)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            command.Execute(null!, new SquadDoctorSettings { Path = Path.GetFullPath("/kw/work"), Global = true }));
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
