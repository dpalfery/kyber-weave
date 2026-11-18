using KyberWeave.Testing.Squad;
using System.Text;
using KyberWeave.Cli;
using KyberWeave.Data.Configuration;
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
public sealed class SquadArbiterCliTests
{
    private readonly InMemorySquadState _state = new();
    private readonly FakeSquadRenderer _renderer = new();
    private readonly FileKyberWeaveConfigRepository _configs;

    public SquadArbiterCliTests() =>
        _configs = new FileKyberWeaveConfigRepository(new FakeRepositoryTreeProbe(), _state.Files, _state.Paths, _state.Environment);

    [Fact]
    public void Install_PassesArbiterSettingsIntoRenderRequest()
    {
        string targetDir = NewDir("arbiter-install-wiring");
        WriteArbiterConfig(targetDir, enabled: true, timeoutMs: 1500);

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, renderer, configs: _configs);

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

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadStateStore stateStore = _state.Store;
        SeedDeployment(targetDir, stateStore, (".codex/agents/architect.toml", "name = \"architect\"\n"));

        SquadUpdateCommand command = SquadTestCommands.InMemoryUpdate(_state, releaseSource, renderer, configs: _configs);

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

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadStateStore stateStore = _state.Store;
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, renderer, configs: _configs);

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
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
                && string.Equals(r.Details, "enforced-global", StringComparison.Ordinal));
    }

    [Fact]
    public void ArbiterProcessProbe_ParsesVersionOutput()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-arbiter", "kyber-weave-arbiter 1.2.3\n");
        var probe = SquadCommandComposition.ResolveArbiterProbe(executor, new FakeProcessEnvironment());

        var result = probe.Probe();

        Assert.True(result.IsAvailable);
        Assert.Equal("1.2.3", result.Version);
    }

    [Fact]
    public void ArbiterProcessProbe_WhenBinaryMissing_ReportsUnavailable()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithFailure("kyber-weave-arbiter", "missing");
        var probe = SquadCommandComposition.ResolveArbiterProbe(executor, new FakeProcessEnvironment());

        var result = probe.Probe();

        Assert.False(result.IsAvailable);
        Assert.Null(result.Version);
    }

    [Fact]
    public void Install_WithArbiterEnabled_NamesTheOpenCodeArbiterServerRegistration()
    {
        string targetDir = NewDir("arbiter-install-opencode");
        WriteArbiterConfig(targetDir, enabled: true, timeoutMs: 3000);

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, renderer, configs: _configs);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["opencode"],
                Global = false,
                DryRun = false,
                Adopt = false,
                Yes = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Register kw-arbiter in opencode.json", execution.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter serve", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOpenCodeArbiterRegistrationStepIsNamedOnlyWhereTheRenderGrantsTheServer()
    {
        Assert.Null(SquadInstallCommand.OpenCodeArbiterRegistrationStep(SquadDeploymentScope.Global));
        Assert.Contains(
            "Register kw-arbiter in opencode.json",
            SquadInstallCommand.OpenCodeArbiterRegistrationStep(SquadDeploymentScope.Project),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Install_PrintsTrustStepsForSelectedTargets()
    {
        string targetDir = NewDir("arbiter-install-trust");
        WriteArbiterConfig(targetDir, enabled: true, timeoutMs: 3000);

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, renderer, configs: _configs);

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

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadStateStore stateStore = _state.Store;
        SeedDeployment(targetDir, stateStore, (".codex/agents/architect.toml", "name = \"architect\"\n"));

        SquadUpdateCommand command = SquadTestCommands.InMemoryUpdate(_state, releaseSource, renderer, configs: _configs);

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
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: workingDir, configs: _configs);

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
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: workingDir, configs: _configs);

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
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: workingDir, configs: _configs);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("KW-ARB-BIN-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>WithMatchingArbiterProbe</c> has to satisfy the real doctor comparison. A stub that
    /// trims the version on its own can drift and bring back <c>KW-ARB-BIN-001</c>.
    /// </summary>
    [Fact]
    public void Doctor_WhenTheMatchingArbiterProbeIsStubbed_DoesNotReportKwArbBin001()
    {
        string workingDir = NewDir("arbiter-doctor-stub");
        WriteArbiterConfig(workingDir, enabled: true, timeoutMs: 3000);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithMatchingArbiterProbe();
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: workingDir, configs: _configs);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("KW-ARB-BIN-001", execution.Output, StringComparison.Ordinal);
        Assert.Contains($"kyber-weave-arbiter {CliVersion()}", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_OnZcodeRepoWithArbiterDisabled_DoesNotRequireTheArbiterServer()
    {
        // A repo with .zcode/ whose source toolchain declares kw-arbiter: with the
        // Arbiter disabled the render grants no arbiter tools, so doctor must not require
        // the server (review 20.1, Major 3).
        string workingDir = NewDir("arbiter-doctor-zcode-off");
        WriteArbiterConfig(workingDir, enabled: false, timeoutMs: 3000);
        WriteDoctorSourceWithArbiterOnlyToolchain(workingDir);
        _state.Files.CreateDirectory(Path.Combine(workingDir, ".zcode"));

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            workingDirectory: workingDir,
            globalRoots: new FixedDoctorRoot(Path.Combine(_state.Files.Root, "doctor-global-root")),
            configs: _configs);

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
        _state.Files.CreateDirectory(Path.Combine(workingDir, ".zcode"));
        _state.Files.CreateDirectory(Path.Combine(_state.Files.Root, "doctor-global-root"));

        string cliVersion = CliVersion();
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithProbeOutput("kyber-weave-arbiter", $"kyber-weave-arbiter {cliVersion}\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            workingDirectory: workingDir,
            globalRoots: new FixedDoctorRoot(Path.Combine(_state.Files.Root, "doctor-global-root")),
            configs: _configs);

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
        _state.Files.CreateDirectory(Path.Combine(workingDir, ".zcode"));
        _state.Files.CreateDirectory(Path.Combine(_state.Files.Root, "doctor-global-root"));

        SquadStateStore stateStore = _state.Store;
        SeedReceipt(workingDir, stateStore, SquadDeploymentScope.Global);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithProbeOutput("kyber-weave-arbiter", $"kyber-weave-arbiter {CliVersion()}\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            workingDirectory: workingDir,
            globalRoots: new FixedDoctorRoot(Path.Combine(_state.Files.Root, "doctor-global-root")),
            configs: _configs);

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
        _state.Files.CreateDirectory(Path.Combine(workingDir, ".zcode"));
        _state.Files.CreateDirectory(Path.Combine(_state.Files.Root, "doctor-global-root"));

        SquadStateStore stateStore = _state.Store;
        SeedReceipt(workingDir, stateStore, SquadDeploymentScope.Project);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithProbeOutput("kyber-weave-arbiter", $"kyber-weave-arbiter {CliVersion()}\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            workingDirectory: workingDir,
            globalRoots: new FixedDoctorRoot(Path.Combine(_state.Files.Root, "doctor-global-root")),
            configs: _configs);

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
    private void WriteDoctorSourceWithArbiterOnlyToolchain(string workingDir)
    {
        string target = Path.Combine(workingDir, "products", "kyber-squad");
        string fixturePath = ArbiterSquadFixture.CreateInMemory(_state.Files).Path;
        foreach (string sourceFile in _state.Files.EnumerateFiles(fixturePath, "*", recursive: true))
        {
            string relative = Path.GetRelativePath(fixturePath, sourceFile);
            string destination = Path.Combine(target, relative);
            _state.Files.CreateDirectory(Path.GetDirectoryName(destination)!);
            _state.Files.WriteAllBytes(destination, _state.Files.ReadAllBytes(sourceFile));
        }

        _state.Files.WriteAllText(
            Path.Combine(target, "toolchain.yml"),
            "schema: kyber-squad.toolchain/v1\n" +
            "required-features:\n" +
            "  - agent-ir/v1\n" +
            "required-mcp-tools:\n" +
            "  kw-arbiter:\n" +
            "    - arbiter_evaluate_agent_dispatch_or_return\n" +
            "    - arbiter_list_effective_rules_by_trigger\n" +
            "    - arbiter_check_configuration_and_key_readiness\n" +
            "validated-release: null\n");
        _state.Files.WriteAllText(Path.Combine(workingDir, "KyberWeave.sln"), string.Empty);
    }

    private sealed class FixedDoctorRoot(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => root;
    }

    private static string CliVersion() =>
        CliAssemblyVersion.ForProbe(typeof(SquadDoctorCommand).Assembly);

    private string NewDir(string name)
    {
        string dir = Path.Combine(_state.Files.Root, "work", name);
        _state.Files.CreateDirectory(dir);
        return dir;
    }

    private void WriteArbiterConfig(string targetRoot, bool enabled, int timeoutMs)
    {
        string configDir = Path.Combine(targetRoot, ".kyber-weave");
        _state.Files.CreateDirectory(configDir);
        string enabledText = enabled ? "true" : "false";
        _state.Files.WriteAllText(
            Path.Combine(configDir, "kyber-weave.yml"),
            $"arbiter:\n  enabled: {enabledText}\n  provider:\n    timeout-ms: {timeoutMs}\n");
    }

    private void SeedDeployment(
        string targetRoot,
        SquadStateStore stateStore,
        params (string RelativePath, string Content)[] files)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project);
        string lockPath = stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Project);
        _state.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relPath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relPath);
            _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            _state.Files.WriteAllBytes(fullPath, bytes);
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

        _state.Files.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt));
        _state.Files.WriteAllText(lockPath, stateStore.SerializeLock(squadLock));
    }

    private void SeedReceipt(string targetRoot, SquadStateStore stateStore, SquadDeploymentScope scope)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, scope);
        _state.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v1",
            Scope: scope,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: []);
        _state.Files.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt));
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }
}
