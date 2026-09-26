using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the <c>-v|--version</c> contract on <c>squad install</c> and <c>squad update</c>
/// (Test contract T1a/T1b/T1c of docs/plans/2026-09-25-squad-install-update-version-flag.md):
/// a pinned version is normalized before the release request is built and reaches the release
/// source stripped of its <c>v</c> prefix and <c>+build</c> metadata; input that is not a
/// release tag is a client error (exit 2) rejected before any network call or filesystem
/// write; and a well-formed version with no matching GitHub release fails closed (exit 1)
/// with a targeted diagnostic instead of the raw 404.
/// </summary>
public sealed class SquadVersionOptionTests : IDisposable
{
    private static readonly Uri ApiRoot = new("https://api.github.test/");
    private readonly TempDirectory _temp = new();

    [Fact]
    public void Install_WithPinnedVersion_PinsTheNormalizedVersionAtTheReleaseSourceAndExitsZero()
    {
        string targetDir = Path.Combine(_temp.Path, "install-pinned");
        Directory.CreateDirectory(targetDir);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Version = "v1.2.3+dev"
            }));

        Assert.Equal(0, execution.ExitCode);
        SquadReleaseRequest request = Assert.Single(releaseSource.Requests);
        Assert.Equal("1.2.3", request.Version);
    }

    [Fact]
    public void Update_WithPinnedVersion_PinsTheNormalizedVersionAtTheReleaseSourceAndExitsZero()
    {
        string targetDir = Path.Combine(_temp.Path, "update-pinned");
        Directory.CreateDirectory(targetDir);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedDeployment(targetDir, stateStore);
        SquadUpdateCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true,
                Version = "v1.2.3+dev"
            }));

        Assert.Equal(0, execution.ExitCode);
        SquadReleaseRequest request = Assert.Single(releaseSource.Requests);
        Assert.Equal("1.2.3", request.Version);
    }

    [Fact]
    public void Install_WithoutPinnedVersion_RequestsTheRunningAssemblyVersionWithoutMetadata()
    {
        string targetDir = Path.Combine(_temp.Path, "install-default-version");
        Directory.CreateDirectory(targetDir);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false
            }));

        Assert.Equal(0, execution.ExitCode);
        SquadReleaseRequest request = Assert.Single(releaseSource.Requests);
        Assert.Equal(ExpectedDefaultVersion(), request.Version);
    }

    [Fact]
    public void Update_WithoutPinnedVersion_RequestsTheRunningAssemblyVersionWithoutMetadata()
    {
        string targetDir = Path.Combine(_temp.Path, "update-default-version");
        Directory.CreateDirectory(targetDir);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedDeployment(targetDir, stateStore);
        SquadUpdateCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true
            }));

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
    public void Install_InvalidPinnedVersion_ExitsTwoWithAnErrorLineBeforeAnyNetworkCallOrFilesystemWrite(
        string version,
        string expectedMessageFragment)
    {
        string targetDir = Path.Combine(_temp.Path, "install-invalid-version");
        Directory.CreateDirectory(targetDir);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer);
        IReadOnlyDictionary<string, byte[]> before = DirectoryTreeSnapshot.SnapshotTree(_temp.Path);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Version = version
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedMessageFragment, execution.Output, StringComparison.Ordinal);
        Assert.Empty(releaseSource.Requests);
        DirectoryTreeSnapshot.AssertTreeUnchanged(_temp.Path, before);
    }

    [Theory]
    [InlineData("../evil", "../evil")]
    [InlineData("0.2.0/evil", "0.2.0/evil")]
    [InlineData("v", "'v'")]
    [InlineData("1.0", "1.0")]
    [InlineData("01.2.3", "01.2.3")]
    [InlineData("1.2.3-01", "1.2.3-01")]
    public void Update_InvalidPinnedVersion_ExitsTwoWithAnErrorLineBeforeAnyNetworkCallOrFilesystemWrite(
        string version,
        string expectedMessageFragment)
    {
        string targetDir = Path.Combine(_temp.Path, "update-invalid-version");
        Directory.CreateDirectory(targetDir);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedDeployment(targetDir, stateStore);
        SquadUpdateCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer);
        IReadOnlyDictionary<string, byte[]> before = DirectoryTreeSnapshot.SnapshotTree(_temp.Path);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true,
                Version = version
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedMessageFragment, execution.Output, StringComparison.Ordinal);
        Assert.Empty(releaseSource.Requests);
        DirectoryTreeSnapshot.AssertTreeUnchanged(_temp.Path, before);
    }

    [Fact]
    public void Install_WithPinnedVersionThatHasNoGitHubRelease_FailsWithTheTargetedDiagnosticAndExitOne()
    {
        string targetDir = Path.Combine(_temp.Path, "install-missing-release");
        Directory.CreateDirectory(targetDir);
        using StatusCodeHandler handler = new StatusCodeHandler(HttpStatusCode.NotFound);
        using GitHubSquadReleaseSource releaseSource = new(handler, ApiRoot);
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Version = "9.9.9"
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9.9.9", execution.Output, StringComparison.Ordinal);
        Assert.Contains("dpalfery/kyber-weave", execution.Output, StringComparison.Ordinal);
        Assert.Contains("releases", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new Uri(ApiRoot, "repos/dpalfery/kyber-weave/releases/tags/v9.9.9"),
            Assert.Single(handler.Requests));
        Assert.False(Directory.Exists(Path.Combine(targetDir, ".kyber-weave")));
    }

    public void Dispose() => _temp.Dispose();

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
    private static void SeedDeployment(string targetRoot, SquadStateStore stateStore)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project);
        string lockPath = stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        string deployedRelativePath = ".codex/agents/architect.toml";
        string deployedPath = Path.Combine(targetRoot, deployedRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(deployedPath)!);
        byte[] deployedBytes = Encoding.UTF8.GetBytes("name = \"architect\"\n");
        File.WriteAllBytes(deployedPath, deployedBytes);

        SquadReceipt receipt = new SquadReceipt(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Project,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
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

        File.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt), Encoding.UTF8);
        File.WriteAllText(lockPath, stateStore.SerializeLock(squadLock), Encoding.UTF8);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
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
