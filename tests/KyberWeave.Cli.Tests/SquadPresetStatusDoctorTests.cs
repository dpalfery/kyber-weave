using KyberWeave.Testing.Squad;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;
using KyberWeave.Data.Squad.Packaging;

namespace KyberWeave.Tests;

/// <summary>
///     Verifies, over an in-memory tree, that <see cref="SquadStatusCommand" /> and <see cref="SquadDoctorCommand" />
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

    private readonly InMemorySquadState _state = new();
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new();

    public SquadPresetStatusDoctorTests() => _ansi.Profile.Width = 200;

    public void Dispose()
    {
        _ansi.Dispose();
        _streams.Dispose();
    }

    private InMemoryFileSystem Files => _state.Files;

    private string Root(string name)
    {
        string path = Path.Combine(Files.Root, name);
        Files.CreateDirectory(path);
        return path;
    }

    /// <summary>A repository root in the in-memory tree that carries the markers <c>squad doctor</c> looks for and the shipped Squad source.</summary>
    private string SquadRepository(string name)
    {
        string root = Root(name);
        Files.WriteAllText(Path.Combine(root, "KyberWeave.sln"), "solution");
        CanonicalSquadTree.CopyInto(Files, Path.Combine(root, "products", "kyber-squad"));
        return root;
    }

    private string Output => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private SquadStatusCommand StatusCommand(string workingDirectory)
    {
        SquadConsole console = new(_ansi, _streams);
        FakeProcessEnvironment environment = new(
            currentDirectory: workingDirectory,
            homeDirectory: _state.UserPaths.HomeDirectory,
            isFileSystemCaseInsensitive: !Files.CaseSensitive);
        return new SquadStatusCommand(
            _state.Store,
            _state.Migration,
            new SquadGlobalRoots(_ => null, _state.UserPaths.HomeDirectory),
            new SquadModelPresetStore(_state.UserPaths, Files),
            _state.Semantics,
            console,
            Files,
            environment,
            new SquadCommandRoots(Files, _state.Paths, environment, console));
    }

    private SquadDoctorCommand DoctorCommand(
        FakeProcessExecutor executor,
        string workingDirectory,
        SquadGlobalRoots globalRoots,
        FakeSquadRenderer renderer)
    {
        SquadConsole console = new(_ansi, _streams);
        FakeProcessEnvironment environment = new(
            currentDirectory: workingDirectory,
            homeDirectory: _state.UserPaths.HomeDirectory,
            isFileSystemCaseInsensitive: !Files.CaseSensitive);
        InMemorySquadServices services = new(Files);
        return new SquadDoctorCommand(
            _state.Store,
            _state.Migration,
            globalRoots,
            renderer,
            new SquadModelPresetStore(_state.UserPaths, Files),
            _state.Planner,
            new SquadResponseGuidanceReader(Files),
            environment,
            new SquadPackSourceLocator(Files, _state.Paths, environment),
            services.SourceLoader(),
            new SquadSkillShadowing(Files, _state.Semantics, environment),
            new DevinImportOverlap(Files, _state.Semantics, environment),
            new ZCodeMcpConfiguration(Files, _state.Semantics, environment),
            new SquadDoctorOptions(executor, workingDirectory),
            _state.Semantics,
            new FakeKyberWeaveConfigRepository(),
            console,
            Files,
            new SquadCommandRoots(Files, _state.Paths, environment, console),
            new FakeMcpWiringService());
    }

    [Fact]
    public void StatusShowsActivePresetPerTargetWhenLockHasPresetsAndDigestMatches()
    {
        // AC 5.3: status output displays active preset per target; clean run exits 0 when preset matches.
        string targetDir = Root("matching-preset-project");

        WritePreset("zai", SamplePresetYaml);
        string digest = ComputeDigest(SamplePresetYaml);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", digest)
        };

        SeedDeployment(targetDir, SquadDeploymentScope.Project, presets,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        int exit = StatusCommand(targetDir).Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            });

        Assert.Equal(0, exit);
        Assert.Contains("claude", Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zai", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("(changed since applied)", Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(missing)", Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusShowsNoneOrBundledWhenLockHasNoPresets()
    {
        // AC 5.3: status shows that no preset is active (none or bundled) when deployment lock has no presets.
        string targetDir = Root("no-preset-project");

        SeedDeployment(targetDir, SquadDeploymentScope.Project, null,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        int exit = StatusCommand(targetDir).Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            });

        Assert.Equal(0, exit);
        bool showsNone = Output.Contains("none", StringComparison.OrdinalIgnoreCase) ||
                         Output.Contains("bundled", StringComparison.OrdinalIgnoreCase);
        Assert.True(showsNone,
            $"Expected status output to mention 'none' or 'bundled' for presets, but got:\n{Output}");
    }

    [Fact]
    public void StatusWarnsChangedSinceAppliedAndExitsZeroWhenPresetDigestDiffersFromLock()
    {
        // When preset file was edited after deployment, status prints a warning but exits 0.
        string targetDir = Root("drifted-preset-project");

        WritePreset("zai", SamplePresetYaml);
        string initialDigest = ComputeDigest(SamplePresetYaml);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", initialDigest)
        };

        SeedDeployment(targetDir, SquadDeploymentScope.Project, presets,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        // Mutate preset on disk so digest no longer matches lock
        WritePreset("zai", ModifiedPresetYaml);

        int exit = StatusCommand(targetDir).Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            });

        Assert.Equal(0, exit);
        Assert.Contains("zai", Output, StringComparison.Ordinal);
        Assert.Contains("(changed since applied)", Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(missing)", Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusNamesMissingPresetAndExitsOneWhenPresetFileDoesNotExist()
    {
        // AC 3.4: WHEN a recorded preset no longer exists THEN status SHALL exit 1 and name the missing preset.
        string targetDir = Root("missing-preset-project");

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", "a1b2c3d4e5f6".PadRight(64, '0'))
        };

        // Note: do not create zai.yml in userPaths presets directory
        SeedDeployment(targetDir, SquadDeploymentScope.Project, presets,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        int exit = StatusCommand(targetDir).Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            });

        Assert.Equal(1, exit);
        Assert.Contains("zai", Output, StringComparison.Ordinal);
        Assert.Contains("(missing)", Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoctorPassesRecordedModelPresetsToComparisonRender()
    {
        // Doctor must supply the deployment lock's ModelPresets to the comparison render request.
        string home = _state.UserPaths.HomeDirectory;
        string repository = SquadRepository("doctor-repository");

        WritePreset("zai", SamplePresetYaml);
        string digest = ComputeDigest(SamplePresetYaml);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", digest)
        };

        // Seed global deployment
        SeedDeployment(home, SquadDeploymentScope.Global, presets,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadGlobalRoots globalRoots = new(_ => null, home);
        FakeSquadRenderer renderer = PresetRenderer();

        SquadDoctorCommand command = DoctorCommand(executor, repository, globalRoots, renderer);

        _ = await command.ExecuteAsync(
            null!,
            new SquadDoctorSettings
            {
                Path = repository,
                Global = true
            },
            CancellationToken.None);

        Assert.NotEmpty(renderer.RenderRequests);
        SquadRenderRequest? claudeRequest =
            renderer.RenderRequests.FirstOrDefault(req => req.Targets.Contains(SquadTarget.Claude));
        Assert.NotNull(claudeRequest);
        Assert.NotNull(claudeRequest.ModelPresets);
        Assert.True(claudeRequest.ModelPresets.ContainsKey(SquadTarget.Claude));
        Assert.Equal("zai", claudeRequest.ModelPresets[SquadTarget.Claude].Name);
    }

    [Fact]
    public async Task DoctorReportsZeroFindingsWhenComparisonRenderMatchesActivePreset()
    {
        // On a clean deployment where files on disk were rendered with an active preset,
        // doctor must pass the active preset into comparison render so it confirms zero findings and exits 0.
        string home = _state.UserPaths.HomeDirectory;
        string repository = SquadRepository("doctor-repository-clean");

        WritePreset("zai", SamplePresetYaml);
        string digest = ComputeDigest(SamplePresetYaml);

        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", digest)
        };

        // Write unmanaged file on disk under Claude global root with preset-rendered content
        string claudeAgentPath = Path.Combine(home, ".claude", "agents", "architect.md");
        Files.CreateDirectory(Path.GetDirectoryName(claudeAgentPath)!);
        Files.WriteAllBytes(claudeAgentPath, Encoding.UTF8.GetBytes("You are architect.\nModel: custom-preset-model\n"));

        // Seed global deployment with lock having the preset, but receipt not owning the file
        // so that CollectUnmanagedCollisions evaluates whether disk matches render.
        SeedDeployment(home, SquadDeploymentScope.Global, presets);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithMatchingArbiterProbe();
        SquadGlobalRoots globalRoots = new(_ => null, home);
        FakeSquadRenderer renderer = PresetRenderer();

        SquadDoctorCommand command = DoctorCommand(executor, repository, globalRoots, renderer);

        int exit = await command.ExecuteAsync(
            null!,
            new SquadDoctorSettings
            {
                Path = repository,
                Global = true
            },
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Global unmanaged collisions: none", Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Doctor found issues", Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusWhenSquadLockIsCorruptExitsWithCodeOneAndReportsError()
    {
        string targetDir = Root("corrupt-lock-project");

        SeedDeployment(targetDir, SquadDeploymentScope.Project, null,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        string lockPath = _state.Store.ResolveLockPath(targetDir, SquadDeploymentScope.Project);
        Files.WriteAllBytes(lockPath, Encoding.UTF8.GetBytes("{ this is corrupt lock yaml : [ unclosed"));

        int exit = StatusCommand(targetDir).Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            });

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("Model presets: none", Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error", Output, StringComparison.OrdinalIgnoreCase);
    }

    private string WritePreset(string name, string yaml)
    {
        string presetsDir = Path.Combine(_state.UserPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Files.CreateDirectory(presetsDir);
        string presetPath = Path.Combine(presetsDir, $"{name}.yml");
        Files.WriteAllBytes(presetPath, Encoding.UTF8.GetBytes(yaml));
        return presetPath;
    }

    private static string ComputeDigest(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private void SeedDeployment(
        string targetRoot,
        SquadDeploymentScope scope,
        IReadOnlyDictionary<SquadTarget, SquadAppliedPreset>? presets = null,
        params (string RelativePath, string Content)[] files)
    {
        SquadStateStore stateStore = _state.Store;
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, scope);
        string lockPath = stateStore.ResolveLockPath(targetRoot, scope);
        Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        Files.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relPath, string content) in files)
        {
            string fullPath = scope == SquadDeploymentScope.Global
                ? Path.Combine(targetRoot, ".claude", relPath)
                : Path.Combine(targetRoot, relPath);
            Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            Files.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ownedFiles.Add(new SquadOwnedFile(relPath, sha256, "claude", false));
        }

        SquadReceipt receipt = new(
            scope == SquadDeploymentScope.Global ? SquadStateSchemas.ReceiptSchemaV2 : SquadStateSchemas.ReceiptSchemaV1,
            scope,
            targetRoot,
            _state.Time.GetUtcNow(),
            [],
            ownedFiles);

        string schema = presets is { Count: > 0 } ? SquadStateSchemas.LockSchemaV2 : SquadStateSchemas.LockSchemaV1;

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

        Files.WriteAllBytes(receiptPath, Encoding.UTF8.GetBytes(stateStore.SerializeReceipt(receipt)));
        Files.WriteAllBytes(lockPath, Encoding.UTF8.GetBytes(stateStore.SerializeLock(squadLock)));
    }

    /// <summary>
    ///     Renders the architect body with the model the request's active preset selects, so doctor's
    ///     comparison render can be observed against a preset-rendered deployment.
    /// </summary>
    private static FakeSquadRenderer PresetRenderer() =>
        FakeSquadRenderer.Responding(
            request => new SquadRenderResult(
                true,
                [.. request.Targets.Select(target =>
                {
                    bool hasPreset = request.ModelPresets?.ContainsKey(target) == true;
                    string modelName = hasPreset ? "custom-preset-model" : "default-bundled-model";
                    return new SquadDeploymentFile(
                        "agents/architect.md",
                        Encoding.UTF8.GetBytes($"You are architect.\nModel: {modelName}\n"),
                        SquadTargetCatalog.GetToken(target));
                })],
                [],
                [],
                []),
            SquadTarget.Claude);
}
