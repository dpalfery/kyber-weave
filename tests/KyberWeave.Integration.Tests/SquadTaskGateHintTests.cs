using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// When <c>squad install</c> or <c>squad update</c> deploys into a project whose
/// configuration declares no <c>review.task-gates</c>, it prints one line naming the
/// <c>docs init</c> step that writes them, and writes no configuration itself.
/// Global scope prints nothing, because there is no project.
/// </summary>
/// <remarks>
/// Each case's install or update runs once for the whole run through
/// <see cref="PreparedSquadInstalls"/>, keyed by the configuration the command read. The test reads the
/// recorded exit code and console output and checks that the prepared project still holds the configuration
/// it started with; it never runs the command or changes the prepared install.
/// </remarks>
public sealed class SquadTaskGateHintTests
{
    private const string HintFragment = "No task gates are declared";
    private const string DocsInitFragment = "kyber-weave docs init";

    /// <summary>
    /// A project with no config file gets the hint, because there are no task gates to find.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithoutConfigFile_PrintsTaskGateHintAndWritesNoConfiguration()
    {
        PreparedCase prepared = InstallCase("install-no-config", configYaml: null, global: false);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(DocsInitFragment, prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// A project whose config has a review section but no task gates still gets the hint.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithConfigButNoTaskGates_PrintsTaskGateHintAndWritesNoConfiguration()
    {
        const string yaml = """
            review:
              gates:
                - id: probe
                  run: [git, --version]
            """;
        PreparedCase prepared = InstallCase("install-empty-gates", yaml, global: false);
        Assert.Empty(KyberWeaveConfigLoader.Load(prepared.TargetRoot).Review.TaskGates);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(DocsInitFragment, prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// A project that already declares task gates gets no hint: there is nothing to write.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithTaskGates_PrintsNoTaskGateHint()
    {
        const string yaml = """
            review:
              task-gates:
                test-dev:
                  - id: probe
                    run: [git, --version]
            """;
        PreparedCase prepared = InstallCase("install-with-gates", yaml, global: false);
        Assert.NotEmpty(KyberWeaveConfigLoader.Load(prepared.TargetRoot).Review.TaskGates);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.DoesNotContain(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// A tiered-rostered project keys its gates by technology, so technology-keyed gates are gates: no hint.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithTechnologyGates_PrintsNoTaskGateHint()
    {
        const string yaml = """
            review:
              technology-gates:
                csharp:
                  - id: probe
                    run: [dotnet, build]
            """;
        PreparedCase prepared = InstallCase("install-with-technology-gates", yaml, global: false);
        Assert.Empty(KyberWeaveConfigLoader.Load(prepared.TargetRoot).Review.TaskGates);
        Assert.NotEmpty(KyberWeaveConfigLoader.Load(prepared.TargetRoot).Review.TechnologyGates);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.DoesNotContain(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// Global scope prints no hint, because there is no project whose gates could be missing.
    /// </summary>
    [Fact]
    public void Install_GlobalScope_PrintsNoTaskGateHint()
    {
        PreparedCase prepared = InstallCase("global", configYaml: null, global: true);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.DoesNotContain(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// A broken config must not fail the install: it still succeeds and prints the hint,
    /// because unreadable gates are the same as no gates for this purpose.
    /// </summary>
    [Fact]
    public void Install_ProjectScopeWithBrokenConfig_SucceedsAndPrintsTaskGateHint()
    {
        PreparedCase prepared = InstallCase("install-broken-config", "review:\n  task-gates: [oops\n", global: false);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(DocsInitFragment, prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// Update prints the same hint as install when the project has no config file.
    /// </summary>
    [Fact]
    public void Update_ProjectScopeWithoutConfigFile_PrintsTaskGateHintAndWritesNoConfiguration()
    {
        PreparedCase prepared = UpdateCase("update-no-config", configYaml: null);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(DocsInitFragment, prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// Update stays silent when the project already declares task gates.
    /// </summary>
    [Fact]
    public void Update_ProjectScopeWithTaskGates_PrintsNoTaskGateHint()
    {
        const string yaml = """
            review:
              task-gates:
                test-dev:
                  - id: probe
                    run: [git, --version]
            """;
        PreparedCase prepared = UpdateCase("update-with-gates", yaml);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.DoesNotContain(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// Update stays silent when the project declares only technology-keyed task gates.
    /// </summary>
    [Fact]
    public void Update_ProjectScopeWithTechnologyGates_PrintsNoTaskGateHint()
    {
        const string yaml = """
            review:
              technology-gates:
                csharp:
                  - id: probe
                    run: [dotnet, build]
            """;
        PreparedCase prepared = UpdateCase("update-with-technology-gates", yaml);
        Assert.Empty(KyberWeaveConfigLoader.Load(prepared.TargetRoot).Review.TaskGates);
        Assert.NotEmpty(KyberWeaveConfigLoader.Load(prepared.TargetRoot).Review.TechnologyGates);

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.DoesNotContain(HintFragment, prepared.Record.StandardOutput, StringComparison.Ordinal);
        prepared.AssertConfigurationUnchanged();
    }

    /// <summary>
    /// Builds the one real project install for <paramref name="recipe"/> and records what it printed and
    /// what the project's configuration files held before and after.
    /// </summary>
    private static PreparedCase InstallCase(string recipe, string? configYaml, bool global)
    {
        string key = Key("install", recipe, configYaml, global);
        return PreparedSquadInstalls.Get(key, () => BuildInstall(key, configYaml, global));
    }

    /// <summary>Builds the one real project update for <paramref name="recipe"/> over a seeded deployment.</summary>
    private static PreparedCase UpdateCase(string recipe, string? configYaml)
    {
        string key = Key("update", recipe, configYaml, global: false);
        return PreparedSquadInstalls.Get(key, () => BuildUpdate(key, configYaml));
    }

    private static string Key(string kind, string recipe, string? configYaml, bool global) =>
        string.Join(
            "/",
            "task-gate-hint",
            kind,
            recipe,
            global ? "global" : "project",
            "codex",
            "1.2.3",
            configYaml is null ? "no-config" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configYaml))));

    private static PreparedCase BuildInstall(string key, string? configYaml, bool global)
    {
        TempDirectory? directory = new();
        try
        {
            string targetDir = Path.Combine(directory.Path, "target");
            Directory.CreateDirectory(targetDir);
            if (configYaml is not null)
                WriteProjectConfig(targetDir, configYaml);

            string homeDir = Path.Combine(directory.Path, "home");
            string globalRoot = Path.Combine(directory.Path, "global-roots", "codex");
            IReadOnlyDictionary<string, byte[]?> before = SnapshotConfigurationFiles(targetDir);
            IReadOnlyDictionary<string, byte[]?> beforeHome = SnapshotConfigurationFiles(homeDir);

            using FakeSquadReleaseSource releaseSource = new();
            FakeSquadRenderer renderer = new();
            FakeSquadUserPaths userPaths = new(homeDir);
            SquadStateStore stateStore = new(userPaths);
            CommandExecution execution = Capture(() => SquadTestCommands.Install(
                userPaths: userPaths,
                stateStore: stateStore,
                releaseSource: releaseSource,
                renderer: renderer,
                isInteractive: false,
                readAnswer: null,
                globalRoots: global ? new CodexOnlyGlobalRoots(globalRoot) : null).Execute(null!, new SquadInstallSettings
                {
                    Path = targetDir,
                    Targets = ["codex"],
                    Global = global,
                    DryRun = false,
                    Adopt = false,
                    Yes = global
                }));

            PreparedCase prepared = new(
                key,
                directory,
                execution,
                before,
                beforeHome,
                targetDir,
                homeDir);
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    private static PreparedCase BuildUpdate(string key, string? configYaml)
    {
        TempDirectory? directory = new();
        try
        {
            string targetDir = Path.Combine(directory.Path, "target");
            Directory.CreateDirectory(targetDir);
            if (configYaml is not null)
                WriteProjectConfig(targetDir, configYaml);

            using FakeSquadReleaseSource releaseSource = new();
            FakeSquadRenderer renderer = new();
            FakeSquadUserPaths userPaths = new(Path.Combine(directory.Path, "home"));
            SquadStateStore stateStore = new(userPaths);
            SeedDeployment(targetDir, stateStore);
            IReadOnlyDictionary<string, byte[]?> before = SnapshotConfigurationFiles(targetDir);

            CommandExecution execution = Capture(() => SquadTestCommands.Update(
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

            PreparedCase prepared = new(
                key,
                directory,
                execution,
                before,
                new Dictionary<string, byte[]?>(StringComparer.Ordinal),
                targetDir,
                userPaths.HomeDirectory);
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
        }
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
        return new CommandExecution(execution.Result, execution.Output, execution.ErrorOutput);
    }

    private sealed record CommandExecution(int ExitCode, string Output, string ErrorOutput);

    /// <summary>
    /// One prepared install or update: its record and the configuration files it started with, so the
    /// "writes no configuration" assertion compares the prepared project's bytes with the bytes it was built from.
    /// </summary>
    private sealed class PreparedCase(
        string key,
        TempDirectory directory,
        CommandExecution execution,
        IReadOnlyDictionary<string, byte[]?> before,
        IReadOnlyDictionary<string, byte[]?> beforeHome,
        string targetRoot,
        string homeRoot) : PreparedSquadInstall(
            key,
            directory,
            new SquadInstallRecord
            {
                ExitCode = execution.ExitCode,
                StandardOutput = execution.Output,
                StandardError = execution.ErrorOutput
            })
    {
        /// <summary>The prepared project root, which holds the configuration the command read.</summary>
        public string TargetRoot => targetRoot;

        public void AssertConfigurationUnchanged()
        {
            AssertConfigurationFilesUnchanged(targetRoot, before);
            AssertConfigurationFilesUnchanged(homeRoot, beforeHome);
        }
    }

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
