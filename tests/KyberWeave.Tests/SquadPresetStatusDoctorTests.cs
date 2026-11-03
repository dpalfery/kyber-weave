using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Integration tests verifying that <see cref="SquadStatusCommand" /> and <see cref="SquadDoctorCommand" />
///     correctly inspect, report, and apply model presets recorded in deployment locks.
///     Acceptance criteria:
///     - 3.4: WHEN a recorded preset no longer exists THEN squad update/status SHALL fail before writing / exit 1 and name
///     the missing preset.
///     - 5.3: squad status SHALL show the active preset per target, or that none is active.
/// </summary>
public sealed class SquadPresetStatusDoctorTests : IDisposable
{
    private const string SamplePresetYaml = """
                                            schema: kyber-squad.model-preset/v1
                                            profiles:
                                              deep-planning:
                                                claude: claude-3-7-sonnet
                                              general:
                                                claude: claude-3-7-sonnet
                                            """;

    private const string ModifiedPresetYaml = """
                                              schema: kyber-squad.model-preset/v1
                                              profiles:
                                                deep-planning:
                                                  claude: claude-3-5-haiku
                                                general:
                                                  claude: claude-3-5-haiku
                                              """;

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void StatusShowsActivePresetPerTargetWhenLockHasPresetsAndDigestMatches()
    {
        // AC 5.3: status output displays active preset per target; clean run exits 0 when preset matches.
        string targetDir = Path.Combine(_temp.Path, "matching-preset-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        WritePreset(userPaths, "zai", SamplePresetYaml);
        string digest = ComputeDigest(SamplePresetYaml);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", digest)
        };

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore, presets,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        SquadStatusCommand command = new(userPaths, stateStore);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(0, execution.Result);
        Assert.Contains("claude", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zai", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("(changed since applied)", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(missing)", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusShowsNoneOrBundledWhenLockHasNoPresets()
    {
        // AC 5.3: status shows that no preset is active (none or bundled) when deployment lock has no presets.
        string targetDir = Path.Combine(_temp.Path, "no-preset-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore, null,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        SquadStatusCommand command = new(userPaths, stateStore);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(0, execution.Result);
        bool showsNone = execution.Output.Contains("none", StringComparison.OrdinalIgnoreCase) ||
                         execution.Output.Contains("bundled", StringComparison.OrdinalIgnoreCase);
        Assert.True(showsNone,
            $"Expected status output to mention 'none' or 'bundled' for presets, but got:\n{execution.Output}");
    }

    [Fact]
    public void StatusWarnsChangedSinceAppliedAndExitsZeroWhenPresetDigestDiffersFromLock()
    {
        // When preset file was edited after deployment, status prints a warning but exits 0.
        string targetDir = Path.Combine(_temp.Path, "drifted-preset-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        WritePreset(userPaths, "zai", SamplePresetYaml);
        string initialDigest = ComputeDigest(SamplePresetYaml);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", initialDigest)
        };

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore, presets,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        // Mutate preset on disk so digest no longer matches lock
        WritePreset(userPaths, "zai", ModifiedPresetYaml);

        SquadStatusCommand command = new(userPaths, stateStore);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(0, execution.Result);
        Assert.Contains("zai", execution.Output, StringComparison.Ordinal);
        Assert.Contains("(changed since applied)", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(missing)", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusNamesMissingPresetAndExitsOneWhenPresetFileDoesNotExist()
    {
        // AC 3.4: WHEN a recorded preset no longer exists THEN status SHALL exit 1 and name the missing preset.
        string targetDir = Path.Combine(_temp.Path, "missing-preset-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", "a1b2c3d4e5f6".PadRight(64, '0'))
        };

        // Note: do not create zai.yml in userPaths presets directory
        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore, presets,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        SquadStatusCommand command = new(userPaths, stateStore);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(1, execution.Result);
        Assert.Contains("zai", execution.Output, StringComparison.Ordinal);
        Assert.Contains("(missing)", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorPassesRecordedModelPresetsToComparisonRender()
    {
        // Doctor must supply the deployment lock's ModelPresets to the comparison render request.
        string tempHome = Path.Combine(_temp.Path, "doctor-home");
        Directory.CreateDirectory(tempHome);

        FakeSquadUserPaths userPaths = new(tempHome);
        SquadStateStore stateStore = new(userPaths);

        WritePreset(userPaths, "zai", SamplePresetYaml);
        string digest = ComputeDigest(SamplePresetYaml);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", digest)
        };

        // Seed global deployment
        SeedDeployment(tempHome, SquadDeploymentScope.Global, stateStore, presets,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        DoctorPresetTestRenderer renderer = new();

        SquadDoctorCommand command = new(
            executor,
            userPaths,
            KyberWeaveTestPaths.ToolRoot,
            globalRoots,
            renderer,
            stateStore);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = KyberWeaveTestPaths.ToolRoot,
                Global = true
            }));

        Assert.NotEmpty(renderer.RenderRequests);
        SquadRenderRequest? claudeRequest =
            renderer.RenderRequests.FirstOrDefault(req => req.Targets.Contains(SquadTarget.Claude));
        Assert.NotNull(claudeRequest);
        Assert.NotNull(claudeRequest.ModelPresets);
        Assert.True(claudeRequest.ModelPresets.ContainsKey(SquadTarget.Claude));
        Assert.Equal("zai", claudeRequest.ModelPresets[SquadTarget.Claude].Name);
    }

    [Fact]
    public void DoctorReportsZeroFindingsWhenComparisonRenderMatchesActivePreset()
    {
        // On a clean deployment where files on disk were rendered with an active preset,
        // doctor must pass the active preset into comparison render so it confirms zero findings and exits 0.
        string tempHome = Path.Combine(_temp.Path, "doctor-home-clean");
        Directory.CreateDirectory(tempHome);

        FakeSquadUserPaths userPaths = new(tempHome);
        SquadStateStore stateStore = new(userPaths);

        WritePreset(userPaths, "zai", SamplePresetYaml);
        string digest = ComputeDigest(SamplePresetYaml);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", digest)
        };

        // Write unmanaged file on disk under Claude global root with preset-rendered content
        string claudeAgentPath = Path.Combine(tempHome, ".claude", "agents", "architect.md");
        Directory.CreateDirectory(Path.GetDirectoryName(claudeAgentPath)!);
        File.WriteAllText(claudeAgentPath, "You are architect.\nModel: custom-preset-model\n", Encoding.UTF8);

        // Seed global deployment with lock having the preset, but receipt not owning the file
        // so that CollectUnmanagedCollisions evaluates whether disk matches render.
        SeedDeployment(tempHome, SquadDeploymentScope.Global, stateStore, presets);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        DoctorPresetTestRenderer renderer = new();

        SquadDoctorCommand command = new(
            executor,
            userPaths,
            KyberWeaveTestPaths.ToolRoot,
            globalRoots,
            renderer,
            stateStore);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = KyberWeaveTestPaths.ToolRoot,
                Global = true
            }));

        Assert.Equal(0, execution.Result);
        Assert.Contains("Global unmanaged collisions: none", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Doctor found issues", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusWhenSquadLockIsCorruptExitsWithCodeOneAndReportsError()
    {
        string targetDir = Path.Combine(_temp.Path, "corrupt-lock-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore, null,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        string lockPath = stateStore.ResolveLockPath(targetDir, SquadDeploymentScope.Project);
        File.WriteAllText(lockPath, "{ this is corrupt lock yaml : [ unclosed", Encoding.UTF8);

        SquadStatusCommand command = new(userPaths, stateStore);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.NotEqual(0, execution.Result);
        Assert.DoesNotContain("Model presets: none", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static string WritePreset(ISquadUserPaths userPaths, string name, string yaml)
    {
        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        string presetPath = Path.Combine(presetsDir, $"{name}.yml");
        File.WriteAllText(presetPath, yaml, Encoding.UTF8);
        return presetPath;
    }

    private static string ComputeDigest(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void SeedDeployment(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadStateStore stateStore,
        IReadOnlyDictionary<SquadTarget, SquadAppliedPreset>? presets = null,
        params (string RelativePath, string Content)[] files)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, scope);
        string lockPath = stateStore.ResolveLockPath(targetRoot, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relPath, string content) in files)
        {
            string fullPath = scope == SquadDeploymentScope.Global
                ? Path.Combine(targetRoot, ".claude", relPath)
                : Path.Combine(targetRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ownedFiles.Add(new SquadOwnedFile(relPath, sha256, "claude", false));
        }

        SquadReceipt receipt = new(
            scope == SquadDeploymentScope.Global ? SquadStateStore.ReceiptSchemaV2 : SquadStateStore.ReceiptSchemaV1,
            scope,
            targetRoot,
            DateTimeOffset.UtcNow,
            [],
            ownedFiles);

        string schema = presets is { Count: > 0 } ? SquadStateStore.LockSchemaV2 : SquadStateStore.LockSchemaV1;

        SquadLock squadLock = new(
            schema,
            "1.2.3",
            "1.2.3",
            "1.2.3",
            "full",
            ["claude"],
            [],
            "best-effort",
            "a".PadRight(64, '0'),
            "b".PadRight(64, '0'),
            new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')),
            presets);

        File.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt), Encoding.UTF8);
        File.WriteAllText(lockPath, stateStore.SerializeLock(squadLock), Encoding.UTF8);
    }

    /// <summary>
    ///     Test renderer that differentiates rendered content when an active preset is provided in the request.
    /// </summary>
    private sealed class DoctorPresetTestRenderer : ISquadRenderer
    {
        private readonly List<SquadRenderRequest> _renderRequests = [];

        public IReadOnlyList<SquadRenderRequest> RenderRequests => _renderRequests;

        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Claude];

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            _renderRequests.Add(request);

            List<SquadDeploymentFile> files = [];
            foreach (SquadTarget target in request.Targets)
            {
                string targetToken = SquadTargetCatalog.GetToken(target);
                bool hasPreset = request.ModelPresets is not null && request.ModelPresets.ContainsKey(target);
                string modelName = hasPreset ? "custom-preset-model" : "default-bundled-model";
                string content = $"You are architect.\nModel: {modelName}\n";
                files.Add(new SquadDeploymentFile("agents/architect.md", Encoding.UTF8.GetBytes(content), targetToken));
            }

            return Task.FromResult(new SquadRenderResult(
                true,
                files,
                [],
                [],
                []));
        }
    }
}
