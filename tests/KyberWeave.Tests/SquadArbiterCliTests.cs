using System.Reflection;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task 6.5 contract: Arbiter settings flow from squad install/update into rendering,
/// unenforced targets are recorded, trust steps are printed, and squad doctor probes
/// the Arbiter binary.
/// </summary>
public sealed class SquadArbiterCliTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Install_PassesArbiterSettingsIntoRenderRequest()
    {
        string targetDir = NewDir("arbiter-install-wiring");
        WriteArbiterConfig(targetDir, enabled: true, timeoutMs: 1500);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Yes = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        SquadRenderRequest request = Assert.Single(renderer.RenderRequests);
        Assert.NotNull(request.Arbiter);
        Assert.True(request.Arbiter.Enabled);
        // ceil(1500 / 1000) + 2 == 4 (Req 22.2, 5.3).
        Assert.Equal(4, request.Arbiter.HookTimeoutSeconds);
    }

    [Fact]
    public void Update_PassesArbiterSettingsIntoRenderRequest()
    {
        string targetDir = NewDir("arbiter-update-wiring");
        WriteArbiterConfig(targetDir, enabled: true, timeoutMs: 1500);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedDeployment(targetDir, stateStore, (".codex/agents/architect.toml", "name = \"architect\"\n"));

        SquadUpdateCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true,
                Yes = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        SquadRenderRequest request = Assert.Single(renderer.RenderRequests);
        Assert.NotNull(request.Arbiter);
        Assert.True(request.Arbiter.Enabled);
        Assert.Equal(4, request.Arbiter.HookTimeoutSeconds);
    }

    [Fact]
    public void Install_WithArbiterEnabled_RecordsArbiterDegradationForFallbackTarget()
    {
        // Task 17.5: antigravity joined the hooked roster, so a project-scope install no
        // longer degrades it; the fallback-only target (warp) still records the code in
        // the receipt — details stay on the renderer/wiring records.
        string targetDir = NewDir("arbiter-lifecycle-nohook");
        WriteArbiterConfig(targetDir, enabled: true, timeoutMs: 3000);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["warp"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Yes = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        SquadReceipt? receipt = stateStore.ReadReceipt(targetDir, SquadDeploymentScope.Project);
        Assert.NotNull(receipt);
        Assert.Contains(
            receipt.Degradations,
            d => string.Equals(d.Code, "arbiter-not-enforced", StringComparison.Ordinal)
                && string.Equals(d.Target, "warp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lifecycle_WithArbiterEnabledGlobalScope_RecordsGlobalScopeDegradation()
    {
        string targetRoot = NewDir("arbiter-lifecycle-global");
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Global,
            Targets: [SquadTarget.Codex],
            Version: "1.0.0",
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)));

        Assert.True(result.Success);
        Assert.Contains(
            result.Degradations ?? [],
            d => string.Equals(d.Code, "arbiter-not-enforced", StringComparison.Ordinal)
                && string.Equals(d.Target, "codex", StringComparison.Ordinal));

        // The global-scope reason comes from the shared target data.
        Assert.Contains(
            ArbiterHookWiring.TargetDegradations([SquadTarget.Codex], SquadDeploymentScope.Global),
            r => string.Equals(r.Code, "arbiter-not-enforced", StringComparison.Ordinal)
                && string.Equals(r.Details, "global-scope", StringComparison.Ordinal));
    }

    [Fact]
    public void ArbiterProcessProbe_ParsesVersionOutput()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-arbiter", "kyber-weave-arbiter 1.2.3\n");
        var probe = SquadCommandComposition.ResolveArbiterProbe(executor);

        var result = probe.Probe();

        Assert.True(result.IsAvailable);
        Assert.Equal("1.2.3", result.Version);
    }

    [Fact]
    public void ArbiterProcessProbe_WhenBinaryMissing_ReportsUnavailable()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithFailure("kyber-weave-arbiter", "missing");
        var probe = SquadCommandComposition.ResolveArbiterProbe(executor);

        var result = probe.Probe();

        Assert.False(result.IsAvailable);
        Assert.Null(result.Version);
    }

    [Fact]
    public void Install_PrintsTrustStepsForSelectedTargets()
    {
        string targetDir = NewDir("arbiter-install-trust");
        WriteArbiterConfig(targetDir, enabled: true, timeoutMs: 3000);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["claude", "copilot"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Yes = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(ArbiterHookWiring.TrustSteps[SquadTarget.Claude], execution.Output, StringComparison.Ordinal);
        Assert.Contains(ArbiterHookWiring.TrustSteps[SquadTarget.Copilot], execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_PrintsTrustStepsForSelectedTargets()
    {
        string targetDir = NewDir("arbiter-update-trust");
        WriteArbiterConfig(targetDir, enabled: true, timeoutMs: 3000);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedDeployment(targetDir, stateStore, (".codex/agents/architect.toml", "name = \"architect\"\n"));

        SquadUpdateCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = ["claude"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true,
                Yes = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(ArbiterHookWiring.TrustSteps[SquadTarget.Claude], execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WhenArbiterEnabledAndProbeFails_ReportsKwArbBin001()
    {
        string workingDir = NewDir("arbiter-doctor-missing");
        WriteArbiterConfig(workingDir, enabled: true, timeoutMs: 3000);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithFailure("kyber-weave-arbiter", "The 'kyber-weave-arbiter' executable is not available on PATH.");
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new(executor, userPaths, workingDirectory: workingDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KW-ARB-BIN-001", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WhenArbiterEnabledAndVersionDiffers_ReportsKwArbBin001()
    {
        string workingDir = NewDir("arbiter-doctor-mismatch");
        WriteArbiterConfig(workingDir, enabled: true, timeoutMs: 3000);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithProbeOutput("kyber-weave-arbiter", "kyber-weave-arbiter 0.0.0\n");
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new(executor, userPaths, workingDirectory: workingDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KW-ARB-BIN-001", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WhenArbiterEnabledAndVersionMatches_Succeeds()
    {
        string workingDir = NewDir("arbiter-doctor-match");
        WriteArbiterConfig(workingDir, enabled: true, timeoutMs: 3000);

        string cliVersion = CliVersion();
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithProbeOutput("kyber-weave-arbiter", $"kyber-weave-arbiter {cliVersion}\n");
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new(executor, userPaths, workingDirectory: workingDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("KW-ARB-BIN-001", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_OnZcodeRepoWithArbiterDisabled_DoesNotRequireTheArbiterServer()
    {
        // A repo with .zcode/ whose source toolchain declares kyber-weave-arbiter: with the
        // Arbiter disabled the render grants no arbiter tools, so doctor must not require
        // the server (review 20.1, Major 3).
        string workingDir = NewDir("arbiter-doctor-zcode-off");
        WriteArbiterConfig(workingDir, enabled: false, timeoutMs: 3000);
        WriteDoctorSourceWithArbiterOnlyToolchain(workingDir);
        Directory.CreateDirectory(Path.Combine(workingDir, ".zcode"));

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new(
            executor,
            userPaths,
            workingDirectory: workingDir,
            globalRoots: new FixedDoctorRoot(Path.Combine(_temp.Path, "doctor-global-root")));

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("kyber-weave-arbiter", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_OnZcodeRepoWithArbiterEnabled_RequiresTheArbiterServer()
    {
        string workingDir = NewDir("arbiter-doctor-zcode-on");
        WriteArbiterConfig(workingDir, enabled: true, timeoutMs: 3000);
        WriteDoctorSourceWithArbiterOnlyToolchain(workingDir);
        Directory.CreateDirectory(Path.Combine(workingDir, ".zcode"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "doctor-global-root"));

        string cliVersion = CliVersion();
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithProbeOutput("kyber-weave-arbiter", $"kyber-weave-arbiter {cliVersion}\n");
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new(
            executor,
            userPaths,
            workingDirectory: workingDir,
            globalRoots: new FixedDoctorRoot(Path.Combine(_temp.Path, "doctor-global-root")));

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("ZCode MCP servers not configured", execution.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_OnZcodeRepoWithArbiterEnabledAndGlobalReceipt_DoesNotRequireTheArbiterServer()
    {
        // The render grants the Arbiter server only to a project-scope wiring, so a
        // global-scope deployment must not be told to configure a server it never granted.
        string workingDir = NewDir("arbiter-doctor-zcode-global");
        WriteArbiterConfig(workingDir, enabled: true, timeoutMs: 3000);
        WriteDoctorSourceWithArbiterOnlyToolchain(workingDir);
        Directory.CreateDirectory(Path.Combine(workingDir, ".zcode"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "doctor-global-root"));

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedReceipt(workingDir, stateStore, SquadDeploymentScope.Global);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithProbeOutput("kyber-weave-arbiter", $"kyber-weave-arbiter {CliVersion()}\n");
        SquadDoctorCommand command = new(
            executor,
            userPaths,
            workingDirectory: workingDir,
            globalRoots: new FixedDoctorRoot(Path.Combine(_temp.Path, "doctor-global-root")),
            stateStore: stateStore);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = true }));

        Assert.DoesNotContain("ZCode MCP servers not configured", execution.Output, StringComparison.Ordinal);
        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void Doctor_OnZcodeRepoWithArbiterEnabledAndProjectReceipt_RequiresTheArbiterServer()
    {
        string workingDir = NewDir("arbiter-doctor-zcode-project-receipt");
        WriteArbiterConfig(workingDir, enabled: true, timeoutMs: 3000);
        WriteDoctorSourceWithArbiterOnlyToolchain(workingDir);
        Directory.CreateDirectory(Path.Combine(workingDir, ".zcode"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "doctor-global-root"));

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedReceipt(workingDir, stateStore, SquadDeploymentScope.Project);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithProbeOutput("kyber-weave-arbiter", $"kyber-weave-arbiter {CliVersion()}\n");
        SquadDoctorCommand command = new(
            executor,
            userPaths,
            workingDirectory: workingDir,
            globalRoots: new FixedDoctorRoot(Path.Combine(_temp.Path, "doctor-global-root")),
            stateStore: stateStore);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("ZCode MCP servers not configured", execution.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>Copies the Arbiter Squad fixture into the doctor's canonical-source
    /// location with a toolchain declaring only the Arbiter server, and drops the
    /// solution marker the pack-source locator keys on.</summary>
    private static void WriteDoctorSourceWithArbiterOnlyToolchain(string workingDir)
    {
        using ArbiterSquadFixture fixture = ArbiterSquadFixture.Create();
        string target = Path.Combine(workingDir, "products", "kyber-squad");
        foreach (string sourceFile in Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(fixture.Path, sourceFile);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination);
        }

        File.WriteAllText(
            Path.Combine(target, "toolchain.yml"),
            "schema: kyber-squad.toolchain/v1\n" +
            "required-features:\n" +
            "  - agent-ir/v1\n" +
            "required-mcp-tools:\n" +
            "  kyber-weave-arbiter:\n" +
            "    - arbiter_evaluate\n" +
            "    - arbiter_rules\n" +
            "    - arbiter_status\n" +
            "validated-release: null\n",
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(workingDir, "KyberWeave.sln"), string.Empty, Encoding.UTF8);
    }

    private sealed class FixedDoctorRoot(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => root;
    }

    private static string CliVersion()
    {
        Assembly assembly = typeof(SquadDoctorCommand).Assembly;
        string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = infoVersion ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            version = version[..plus];
        }

        return version.Trim().TrimStart('v');
    }

    private string NewDir(string name)
    {
        string dir = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteArbiterConfig(string targetRoot, bool enabled, int timeoutMs)
    {
        string configDir = Path.Combine(targetRoot, ".kyber-weave");
        Directory.CreateDirectory(configDir);
        string enabledText = enabled ? "true" : "false";
        File.WriteAllText(
            Path.Combine(configDir, "kyber-weave.yml"),
            $"arbiter:\n  enabled: {enabledText}\n  provider:\n    timeout-ms: {timeoutMs}\n",
            Encoding.UTF8);
    }

    private static void SeedDeployment(
        string targetRoot,
        SquadStateStore stateStore,
        params (string RelativePath, string Content)[] files)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project);
        string lockPath = stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relPath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            ownedFiles.Add(new SquadOwnedFile(relPath, sha256, "codex", Adopted: false));
        }

        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Project,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: ownedFiles);
        SquadLock squadLock = new(
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

    private static void SeedReceipt(string targetRoot, SquadStateStore stateStore, SquadDeploymentScope scope)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v1",
            Scope: scope,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: []);
        File.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt), Encoding.UTF8);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }
}
