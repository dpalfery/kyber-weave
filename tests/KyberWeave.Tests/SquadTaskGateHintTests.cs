using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// When <c>squad install</c> or <c>squad update</c> deploys into a project whose
/// configuration declares no <c>review.task-gates</c>, it prints one line naming the
/// <c>docs init</c> step that writes them, and writes no configuration itself.
/// Global scope prints nothing, because there is no project.
/// </summary>
public sealed class SquadTaskGateHintTests : IDisposable
{
    private const string HintFragment = "No task gates are declared";
    private const string DocsInitFragment = "kyber-weave docs init";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A project with no config file gets the hint, because there are no task gates to find.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithoutConfigFile_PrintsTaskGateHintAndWritesNoConfiguration()
    {
        string targetDir = NewTargetDir("install-no-config");
        IReadOnlyDictionary<string, byte[]?> before = SnapshotConfigurationFiles(targetDir);
        CommandExecution execution = RunInstall(targetDir, global: false);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(HintFragment, execution.Output, StringComparison.Ordinal);
        Assert.Contains(DocsInitFragment, execution.Output, StringComparison.OrdinalIgnoreCase);
        AssertConfigurationFilesUnchanged(targetDir, before);
    }

    /// <summary>
    /// A project whose config has a review section but no task gates still gets the hint.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithConfigButNoTaskGates_PrintsTaskGateHintAndWritesNoConfiguration()
    {
        string targetDir = NewTargetDir("install-empty-gates");
        WriteProjectConfig(targetDir, """
            review:
              gates:
                - id: probe
                  run: [git, --version]
            """);
        Assert.Empty(KyberWeaveConfigLoader.Load(targetDir).Review.TaskGates);
        IReadOnlyDictionary<string, byte[]?> before = SnapshotConfigurationFiles(targetDir);

        CommandExecution execution = RunInstall(targetDir, global: false);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(HintFragment, execution.Output, StringComparison.Ordinal);
        Assert.Contains(DocsInitFragment, execution.Output, StringComparison.OrdinalIgnoreCase);
        AssertConfigurationFilesUnchanged(targetDir, before);
    }

    /// <summary>
    /// A project that already declares task gates gets no hint: there is nothing to write.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithTaskGates_PrintsNoTaskGateHint()
    {
        string targetDir = NewTargetDir("install-with-gates");
        WriteProjectConfig(targetDir, """
            review:
              task-gates:
                test-dev:
                  - id: probe
                    run: [git, --version]
            """);
        Assert.NotEmpty(KyberWeaveConfigLoader.Load(targetDir).Review.TaskGates);
        IReadOnlyDictionary<string, byte[]?> before = SnapshotConfigurationFiles(targetDir);

        CommandExecution execution = RunInstall(targetDir, global: false);

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain(HintFragment, execution.Output, StringComparison.Ordinal);
        AssertConfigurationFilesUnchanged(targetDir, before);
    }

    /// <summary>
    /// Global scope prints no hint, because there is no project whose gates could be missing.
    /// </summary>
    [Fact]
    public void Install_GlobalScope_PrintsNoTaskGateHint()
    {
        string anchorDir = NewTargetDir("global-anchor");
        string homeDir = Path.Combine(_temp.Path, "global-home");
        string globalRoot = Path.Combine(_temp.Path, "global-roots", "codex");
        IReadOnlyDictionary<string, byte[]?> beforeAnchor = SnapshotConfigurationFiles(anchorDir);
        IReadOnlyDictionary<string, byte[]?> beforeHome = SnapshotConfigurationFiles(homeDir);

        CommandExecution execution = RunGlobalInstall(anchorDir, homeDir, globalRoot);

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain(HintFragment, execution.Output, StringComparison.Ordinal);
        AssertConfigurationFilesUnchanged(anchorDir, beforeAnchor);
        AssertConfigurationFilesUnchanged(homeDir, beforeHome);
    }

    /// <summary>
    /// A broken config must not fail the install: it still succeeds and prints the hint,
    /// because unreadable gates are the same as no gates for this purpose.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithBrokenConfig_SucceedsAndPrintsTaskGateHint()
    {
        string targetDir = NewTargetDir("install-broken-config");
        WriteProjectConfig(targetDir, "review:\n  task-gates: [oops\n");
        Assert.False(KyberWeaveConfigLoader.TryLoad(targetDir).Success);
        IReadOnlyDictionary<string, byte[]?> before = SnapshotConfigurationFiles(targetDir);

        CommandExecution execution = RunInstall(targetDir, global: false);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(HintFragment, execution.Output, StringComparison.Ordinal);
        Assert.Contains(DocsInitFragment, execution.Output, StringComparison.OrdinalIgnoreCase);
        AssertConfigurationFilesUnchanged(targetDir, before);
    }

    /// <summary>
    /// Update prints the same hint as install when the project has no config file.
    /// </summary>
    [Fact]
    public void Update_ProjectScopeWithoutConfigFile_PrintsTaskGateHintAndWritesNoConfiguration()
    {
        string targetDir = NewTargetDir("update-no-config");
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "update-no-config-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedDeployment(targetDir, stateStore);
        IReadOnlyDictionary<string, byte[]?> before = SnapshotConfigurationFiles(targetDir);

        CommandExecution execution = Capture(() => new SquadUpdateCommand(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false).Execute(null!, new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(HintFragment, execution.Output, StringComparison.Ordinal);
        Assert.Contains(DocsInitFragment, execution.Output, StringComparison.OrdinalIgnoreCase);
        AssertConfigurationFilesUnchanged(targetDir, before);
    }

    /// <summary>
    /// Update stays silent when the project already declares task gates.
    /// </summary>
    [Fact]
    public void Update_ProjectScopeWithTaskGates_PrintsNoTaskGateHint()
    {
        string targetDir = NewTargetDir("update-with-gates");
        WriteProjectConfig(targetDir, """
            review:
              task-gates:
                test-dev:
                  - id: probe
                    run: [git, --version]
            """);
        Assert.NotEmpty(KyberWeaveConfigLoader.Load(targetDir).Review.TaskGates);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "update-with-gates-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedDeployment(targetDir, stateStore);
        IReadOnlyDictionary<string, byte[]?> before = SnapshotConfigurationFiles(targetDir);

        CommandExecution execution = Capture(() => new SquadUpdateCommand(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false).Execute(null!, new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain(HintFragment, execution.Output, StringComparison.Ordinal);
        AssertConfigurationFilesUnchanged(targetDir, before);
    }

    private string NewTargetDir(string name)
    {
        string targetDir = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(targetDir);
        return targetDir;
    }

    private CommandExecution RunInstall(string targetDir, bool global)
    {
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "install-home"));
        SquadStateStore stateStore = new(userPaths);

        return Capture(() => new SquadInstallCommand(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false).Execute(null!, new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = global,
                DryRun = false,
                Adopt = false
            }));
    }

    private CommandExecution RunGlobalInstall(string anchorDir, string homeDir, string globalRoot)
    {
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(homeDir);
        SquadStateStore stateStore = new(userPaths);

        return Capture(() => new SquadInstallCommand(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false,
            readAnswer: null,
            globalRoots: new CodexOnlyGlobalRoots(globalRoot)).Execute(null!, new SquadInstallSettings
            {
                Path = anchorDir,
                Targets = ["codex"],
                Global = true,
                DryRun = false,
                Adopt = false,
                Yes = true
            }));
    }

    private static void WriteProjectConfig(string projectRoot, string yaml)
    {
        string path = Path.Combine(projectRoot, ".kyber-weave", "kyber-weave.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml, Encoding.UTF8);
    }

    /// <summary>
    /// Snapshots the host configuration files a squad verb must never write: the default
    /// and legacy config paths under <paramref name="root" />. Deployment state lives
    /// under the fake home and deployed files under their target paths, so neither is
    /// part of this "writes no configuration" gate.
    /// </summary>
    private static IReadOnlyDictionary<string, byte[]?> SnapshotConfigurationFiles(string root)
    {
        Dictionary<string, byte[]?> snapshot = new(StringComparer.Ordinal);
        foreach (string relative in new[]
                 {
                     Path.Combine(".kyber-weave", "kyber-weave.yml"),
                     "kyber-weave.yml"
                 })
        {
            string path = Path.Combine(root, relative);
            snapshot[relative] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        return snapshot;
    }

    private static void AssertConfigurationFilesUnchanged(
        string root,
        IReadOnlyDictionary<string, byte[]?> before)
    {
        foreach ((string relative, byte[]? contents) in before)
        {
            string path = Path.Combine(root, relative);
            if (contents is null)
            {
                Assert.False(File.Exists(path), $"Squad wrote configuration it must not write: {path}");
            }
            else
            {
                Assert.True(File.Exists(path), $"Squad removed host configuration: {path}");
                Assert.Equal(contents, File.ReadAllBytes(path));
            }
        }
    }

    /// <summary>
    /// Seeds a one-file project-scope deployment (receipt plus lock) so <c>squad update</c>
    /// has a deployment to operate on, mirroring the fixture the existing update command
    /// tests seed through <see cref="SquadStateStore" />.
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
    /// Keeps a global install inside the temp directory: the one deployed target resolves
    /// beneath the fixture root instead of the real profile, so the suite never touches
    /// the operator's home.
    /// </summary>
    private sealed class CodexOnlyGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget requested) =>
            requested == SquadTarget.Codex
                ? root
                : throw new ArgumentOutOfRangeException(nameof(requested), requested, "No root mapped for target.");
    }
}
