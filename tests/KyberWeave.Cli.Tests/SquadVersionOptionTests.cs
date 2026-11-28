using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Application.Squad.Release;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the <c>-v|--version</c> contract on <c>squad install</c> and <c>squad update</c>
/// (Test contract T1a/T1b/T1c of docs/archive/plans/2026-09-25-squad-install-update-version-flag.md):
/// a pinned version is normalized before the release request is built and reaches the release
/// source stripped of its <c>v</c> prefix and <c>+build</c> metadata; input that is not a
/// release tag, or that is blank, or whose <c>+build</c> metadata is malformed, is a client
/// error (exit 2) rejected before any network call or filesystem write; and a well-formed
/// version with no
/// matching GitHub release fails closed (exit 1) with a targeted diagnostic instead of the
/// raw 404.
/// </summary>
public sealed class SquadVersionOptionTests
{
    private static readonly Uri ApiRoot = new("https://api.github.test/");
    private readonly InMemorySquadState _state = new();

    [Theory]
    [InlineData("v1.2.3+dev")]
    [InlineData("1.2.3+a.b.9")]
    public async Task Install_WithPinnedVersion_PinsTheNormalizedVersionAtTheReleaseSourceAndExitsZero(string version)
    {
        string targetDir = _state.CreateRoot("install-pinned");
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, renderer);

        CommandExecution execution = await Capture(() => command.ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Version = version
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        SquadReleaseRequest request = Assert.Single(releaseSource.Requests);
        Assert.Equal("1.2.3", request.Version);
    }

    [Fact]
    public async Task Update_WithPinnedVersion_PinsTheNormalizedVersionAtTheReleaseSourceAndExitsZero()
    {
        string targetDir = _state.CreateRoot("update-pinned");
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SeedDeployment(targetDir);
        SquadUpdateCommand command = SquadTestCommands.InMemoryUpdate(_state, releaseSource, renderer);

        CommandExecution execution = await Capture(() => command.ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true,
                Version = "v1.2.3+dev"
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        SquadReleaseRequest request = Assert.Single(releaseSource.Requests);
        Assert.Equal("1.2.3", request.Version);
    }

    [Fact]
    public async Task Install_WithoutPinnedVersion_RequestsTheRunningAssemblyVersionWithoutMetadata()
    {
        string targetDir = _state.CreateRoot("install-default-version");
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, renderer);

        CommandExecution execution = await Capture(() => command.ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        SquadReleaseRequest request = Assert.Single(releaseSource.Requests);
        Assert.Equal(ExpectedDefaultVersion(), request.Version);
    }

    [Fact]
    public async Task Update_WithoutPinnedVersion_RequestsTheRunningAssemblyVersionWithoutMetadata()
    {
        string targetDir = _state.CreateRoot("update-default-version");
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SeedDeployment(targetDir);
        SquadUpdateCommand command = SquadTestCommands.InMemoryUpdate(_state, releaseSource, renderer);

        CommandExecution execution = await Capture(() => command.ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        SquadReleaseRequest request = Assert.Single(releaseSource.Requests);
        Assert.Equal(ExpectedDefaultVersion(), request.Version);
    }

    [Theory]
    [InlineData("../evil", "../evil")]
    [InlineData("0.2.0/evil", "0.2.0/evil")]
    [InlineData("v", "'v'")]
    [InlineData("1.0", "1.0")]
    [InlineData("01.2.3", "01.2.3")]
    [InlineData("1.2.3-01", "1.2.3-01")]
    [InlineData("1.2.3+", "1.2.3+")]
    [InlineData("1.2.3+a..b", "1.2.3+a..b")]
    [InlineData("1.2.3+a b", "1.2.3+a b")]
    // Whitespace-only input is an explicit --version the operator typed; it must be
    // rejected as client input, not silently fall through to the omitted-flag default.
    [InlineData(" ", "--version")]
    [InlineData("\t", "--version")]
    public async Task Install_InvalidPinnedVersion_ExitsTwoWithAnErrorLineBeforeAnyNetworkCallOrFilesystemWrite(
        string version,
        string expectedMessageFragment)
    {
        string targetDir = _state.CreateRoot("install-invalid-version");
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, renderer);
        IReadOnlyDictionary<string, SquadTreeEntry> before = _state.Capture();

        CommandExecution execution = await Capture(() => command.ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Version = version
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedMessageFragment, execution.Output, StringComparison.Ordinal);
        Assert.Empty(releaseSource.Requests);
        Assert.Null(SquadTreeComparison.Difference(before, _state.Capture()));
    }

    [Theory]
    [InlineData("../evil", "../evil")]
    [InlineData("0.2.0/evil", "0.2.0/evil")]
    [InlineData("v", "'v'")]
    [InlineData("1.0", "1.0")]
    [InlineData("01.2.3", "01.2.3")]
    [InlineData("1.2.3-01", "1.2.3-01")]
    [InlineData("1.2.3+", "1.2.3+")]
    [InlineData("1.2.3+a..b", "1.2.3+a..b")]
    [InlineData("1.2.3+a b", "1.2.3+a b")]
    // Whitespace-only input is an explicit --version the operator typed; it must be
    // rejected as client input, not silently fall through to the omitted-flag default.
    [InlineData(" ", "--version")]
    [InlineData("\t", "--version")]
    public async Task Update_InvalidPinnedVersion_ExitsTwoWithAnErrorLineBeforeAnyNetworkCallOrFilesystemWrite(
        string version,
        string expectedMessageFragment)
    {
        string targetDir = _state.CreateRoot("update-invalid-version");
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SeedDeployment(targetDir);
        SquadUpdateCommand command = SquadTestCommands.InMemoryUpdate(_state, releaseSource, renderer);
        IReadOnlyDictionary<string, SquadTreeEntry> before = _state.Capture();

        CommandExecution execution = await Capture(() => command.ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true,
                Version = version
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedMessageFragment, execution.Output, StringComparison.Ordinal);
        Assert.Empty(releaseSource.Requests);
        Assert.Null(SquadTreeComparison.Difference(before, _state.Capture()));
    }

    [Fact]
    public async Task Install_WithPinnedVersionThatHasNoGitHubRelease_FailsWithTheTargetedDiagnosticAndExitOne()
    {
        string targetDir = _state.CreateRoot("install-missing-release");
        using StatusCodeHandler handler = new StatusCodeHandler(HttpStatusCode.NotFound);
        using GitHubSquadReleaseSource releaseSource = RealSquadReleaseSource.CreateInMemory(handler, ApiRoot, _state);
        FakeSquadRenderer renderer = new();
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, renderer);

        CommandExecution execution = await Capture(() => command.ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Version = "9.9.9"
            },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9.9.9", execution.Output, StringComparison.Ordinal);
        Assert.Contains("dpalfery/kyber-weave", execution.Output, StringComparison.Ordinal);
        Assert.Contains("releases", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new Uri(ApiRoot, "repos/dpalfery/kyber-weave/releases/tags/v9.9.9"),
            Assert.Single(handler.Requests));
        Assert.False(_state.Files.DirectoryExists(Path.Combine(targetDir, ".kyber-weave")));
    }

    /// <summary>
    /// Computes, from the assembly attribute, the version the lifecycle resolves when no
    /// pinned version is supplied — the informational version with <c>+metadata</c> stripped —
    /// so the assertion tracks the build stamp instead of hardcoding it.
    /// </summary>
    private static string ExpectedDefaultVersion()
    {
        Assembly assembly = typeof(SquadLifecycleService).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIndex > 0 ? informationalVersion[..plusIndex] : informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "0.1.0";
    }

    /// <summary>
    /// Seeds a one-file project-scope deployment (receipt plus lock) so <c>squad update</c>
    /// has a deployment to operate on, mirroring the fixture the existing update command
    /// tests seed through <see cref="SquadStateStore"/>.
    /// </summary>
    private void SeedDeployment(string targetRoot)
    {
        SquadStateStore stateStore = _state.Store;
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project);
        string lockPath = stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Project);
        _state.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        string deployedRelativePath = ".codex/agents/architect.toml";
        string deployedPath = Path.Combine(targetRoot, deployedRelativePath);
        _state.Files.CreateDirectory(Path.GetDirectoryName(deployedPath)!);
        byte[] deployedBytes = Encoding.UTF8.GetBytes("name = \"architect\"\n");
        _state.Files.WriteAllBytes(deployedPath, deployedBytes);

        SquadReceipt receipt = new SquadReceipt(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Project,
            TargetRoot: ".",
            InstalledAtUtc: _state.Time.GetUtcNow(),
            Degradations: [],
            Files:
            [
                new SquadOwnedFile(
                    deployedRelativePath,
                    Convert.ToHexStringLower(SHA256.HashData(deployedBytes)),
                    "codex",
                    Adopted: false)
            ]);

        SquadLock squadLock = new SquadLock(
            Schema: "kyber-squad.lock/v1",
            SquadVersion: "1.2.3",
            CliVersion: "1.2.3",
            McpVersion: "1.2.3",
            Bundle: "full",
            Targets: ["codex"],
            Exclusions: [],
            Translation: "best-effort",
            BundleDigest: "a".PadRight(64, '0'),
            AssetDigest: "b".PadRight(64, '0'),
            Apm: new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));

        _state.Files.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt));
        _state.Files.WriteAllText(lockPath, stateStore.SerializeLock(squadLock));
    }

    private static async Task<CommandExecution> Capture(Func<Task<int>> execute)
    {
        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    /// <summary>
    /// Answers every request with one fixed status so a pinned-version lookup can be driven
    /// to a GitHub 404 (or a non-404 failure) without a network, while recording each URI.
    /// </summary>
    private sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            Requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
