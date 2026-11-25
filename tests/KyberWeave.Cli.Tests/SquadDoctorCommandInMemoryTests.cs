using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>squad doctor</c> reports over an in-memory Squad state and an in-memory copy of the shipped
/// source: the source-less layout, renderer coverage, the MCP probe, the Devin duplicate-import check, the
/// global collision scan (clean, colliding, invalid source, corrupt or blank receipts) and the install-time
/// unmanaged-collision refusal. No test here touches the disk or starts a program.
/// </summary>
public sealed class SquadDoctorCommandInMemoryTests
{
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly InMemorySquadState _state = new();
    private readonly SquadConsole _console;

    public SquadDoctorCommandInMemoryTests()
    {
        _ansi.Profile.Width = 100_000;
        _console = new SquadConsole(_ansi, new FakeConsole());
    }

    [Fact]
    public void Doctor_SourceLessInstalledLayout_DoesNotReportMissingCanonicalSource()
    {
        string installedDir = CreateDirectory("sourceless-installed-layout");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("apm", "apm, version 0.28.0\n")
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(executor, installedDir, new SquadDoctorSettings
        {
            Path = installedDir,
            Global = false
        });

        string output = Output();
        Assert.DoesNotContain("products/kyber-squad", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing canonical source", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_ReportsRendererCoverageAndMcpProbeStatus()
    {
        string workingDir = CreateDirectory("doctor-workdir");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(executor, workingDir, new SquadDoctorSettings
        {
            Path = workingDir,
            Global = false
        });

        string normalizedOutput = Normalize(Output());
        string availableSection = ExtractDoctorSection(normalizedOutput, "Renderers available:", "Kyber-Weave MCP:");
        Assert.Contains("cursor", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("antigravity", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copilot", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opencode", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kilo", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("factory", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warp", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("codex", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zcode", availableSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("devin", availableSection, StringComparison.OrdinalIgnoreCase);

        // A plain substring check would false-positive here: "copilot" contains "pi" at
        // index 2-3, so the pi renderer's presence has to be asserted as a whole word.
        Assert.Matches(@"\bpi\b", availableSection);
        Assert.DoesNotContain("Not yet implemented:", normalizedOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A workspace holding a Squad skill under both <c>.devin/skills/</c> and
    /// <c>.claude/skills/</c> loads it twice in Devin, which imports Claude's skills by default.
    /// Doctor warns with the remedy but does not fail: both copies work.
    /// </summary>
    [Fact]
    public void Doctor_DevinWorkspace_WarnsOnSkillsDevinWouldLoadTwice()
    {
        string workingDir = CreateDirectory("doctor-devin-overlap");
        foreach (string root in new[] { ".devin", ".claude" })
        {
            string skillDirectory = Path.Combine(workingDir, root, "skills", "code-review");
            _state.Files.CreateDirectory(skillDirectory);
            _state.Files.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: code-review\n---\n");
        }

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(
            executor,
            workingDir,
            new SquadDoctorSettings
            {
                Path = workingDir,
                Global = false
            },
            new FakeSquadGlobalRootResolver(_state.UserPaths.HomeDirectory));

        string normalizedOutput = Normalize(Output());
        Assert.Equal(0, exitCode);
        Assert.Contains("warn", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".claude/skills/", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("code-review", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("\"claude\": false", normalizedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WorkspaceWithoutDevin_SkipsTheDuplicateImportCheck()
    {
        string workingDir = CreateDirectory("doctor-no-devin");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(executor, workingDir, new SquadDoctorSettings
        {
            Path = workingDir,
            Global = false
        });

        Assert.Contains("Devin duplicate imports: not checked", Normalize(Output()), StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WhenMcpProbeFails_ReportsFailureReason()
    {
        string workingDir = CreateDirectory("doctor-missing-mcp");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithFailure("kyber-weave-mcp", "The 'kyber-weave-mcp' executable is not available on PATH.");

        int exitCode = Run(executor, workingDir, new SquadDoctorSettings
        {
            Path = workingDir,
            Global = false
        });

        // Two assertions rather than one contiguous phrase: Spectre.Console wraps
        // MarkupLine output at the console width, and "kyber-weave-mcp ... not
        // available on PATH." is long enough to wrap mid-phrase.
        string output = Output();
        Assert.Contains("kyber-weave-mcp", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not available on", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WhenInsideRepository_ValidatesLocalSource()
    {
        string repoPath = CreateCanonicalRepository("doctor-repository");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("apm", "apm, version 0.28.0\n")
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(executor, repoPath, new SquadDoctorSettings
        {
            Path = repoPath,
            Global = false
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("canonical source", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_GlobalScope_WarnsOnUnmanagedNameCollidingFile()
    {
        string tempHome = CreateDirectory("doctor-global-home");
        string collidingRelative = Path.Combine("agents", "architect.md");
        string collidingFull = Path.Combine(tempHome, ".pi", "agent", collidingRelative);
        WriteFile(collidingFull, "not-the-canonical-architect-bytes");

        string factoryCollidingFull = Path.Combine(tempHome, ".factory", "droids", "architect.md");
        WriteFile(factoryCollidingFull, "not-the-canonical-factory-architect-bytes");

        string repoPath = CreateCanonicalRepository("doctor-global-collision-repository");
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(
            executor,
            repoPath,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            },
            new SquadGlobalRoots(_ => null, tempHome, isWindows: false));

        string normalizedOutput = Normalize(Output());
        Assert.Contains("warn", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("architect", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("agents/architect.md", normalizedOutput.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("canonical identity", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no verified global root", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("droids/architect.md", normalizedOutput.Replace('\\', '/'), StringComparison.Ordinal);
    }

    /// <summary>
    /// A global doctor scan renders Antigravity with the same absolute <c>kyber-weave-mcp</c> that install resolves, so
    /// the expected agent files match what install wrote; without the resolved path the scan would expect the bare name.
    /// </summary>
    [Fact]
    public void Doctor_GlobalScope_RendersAntigravityWithTheResolvedDocsServerCommand()
    {
        const string resolved = "/opt/kyber/bin/kyber-weave-mcp";
        string tempHome = CreateDirectory("doctor-agy-home");
        string repoPath = CreateCanonicalRepository("doctor-agy-repository");
        RecordingRenderer renderer = new(new SquadRendererRegistry(
            [new AntigravityRenderer()],
            new InMemorySquadServices(_state.Files).RenderInputReader()));
        ScriptedProcessRunner processes = new ScriptedProcessRunner()
            .WithProgramOnPath(SquadDocsServerCommand.Executable, resolved);

        SquadTestCommands.InMemoryDoctor(
            _state,
            new FakeProcessExecutor().WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n"),
            repoPath,
            new SquadGlobalRoots(_ => null, tempHome, isWindows: false),
            renderer: renderer,
            output: _console,
            processRunner: processes).Execute(null!, new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            });

        List<SquadRenderRequest> antigravity = renderer.Requests
            .Where(request => request.Scope == SquadDeploymentScope.Global
                && request.Targets.Contains(SquadTarget.Antigravity))
            .ToList();
        Assert.NotEmpty(antigravity);
        Assert.All(antigravity, request => Assert.Equal(resolved, request.DocsServerCommand));
    }

    /// <summary>Forwards every render to the real renderer and keeps the requests it was given.</summary>
    private sealed class RecordingRenderer(ISquadRenderer inner) : ISquadRenderer
    {
        private readonly List<SquadRenderRequest> _requests = [];

        public IReadOnlyList<SquadRenderRequest> Requests => _requests;

        public IReadOnlyCollection<SquadTarget> SupportedTargets => inner.SupportedTargets;

        public Task<SquadRenderResult> RenderAsync(SquadRenderRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            return inner.RenderAsync(request, cancellationToken);
        }
    }

    [Fact]
    public void Doctor_GlobalScope_ReportsRelativeOverrideAndExitsNonZero()
    {
        string tempHome = CreateDirectory("doctor-relative-override-home");
        string repoPath = CreateCanonicalRepository("doctor-relative-override-repository");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadGlobalRoots globalRoots = new(
            name => string.Equals(name, "CLAUDE_CONFIG_DIR", StringComparison.Ordinal)
                ? "relative-dir"
                : null,
            tempHome);

        int exitCode = Run(
            executor,
            repoPath,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            },
            globalRoots);

        string normalizedOutput = Normalize(Output());
        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid global root", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fully qualified", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Global unmanaged collisions", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doctor found issues", normalizedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_GlobalScope_SkipsCollisionScanWhenCanonicalSourceIsInvalid()
    {
        string repoPath = CreateDirectory("invalid-squad-repo");
        _state.Files.CreateDirectory(Path.Combine(repoPath, "products", "kyber-squad"));
        _state.Files.WriteAllText(Path.Combine(repoPath, "KyberWeave.sln"), string.Empty);
        _state.Files.WriteAllText(
            Path.Combine(repoPath, "products", "kyber-squad", "squad.yml"),
            "this is not a valid squad manifest\n");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(
            executor,
            repoPath,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            },
            new SquadGlobalRoots(_ => null, CreateDirectory("invalid-source-home")));

        string output = Output();
        Assert.Equal(1, exitCode);
        Assert.Contains("Canonical source", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scan skipped", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Global unmanaged collisions: none", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_GlobalScope_CleanInstall_ReportsNoUnmanagedCollisions()
    {
        string repoPath = CreateCanonicalRepository("doctor-clean-install-repository");
        string tempHome = CreateDirectory("doctor-clean-install-home");
        SquadGlobalRoots globalRoots = new(_ => null, tempHome, isWindows: false);
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithMatchingArbiterProbe();

        // The install is not this test's subject: it needs a clean global deployment of Cursor to
        // edit, and the in-memory lifecycle installs one from the shipped source in the same tree.
        await InstallGlobalCursorAsync(repoPath, tempHome, globalRoots);

        // Simulate a file recorded in the receipt whose bytes differ from the local source render
        // (the authoritative repro in issue #283: receipt-managed file flagged as unmanaged by doctor)
        string csharpDevFile = Path.Combine(tempHome, ".cursor", "agents", "csharp-dev.md");
        string priorBytes = "prior-release-csharp-dev-content";
        _state.Files.WriteAllText(csharpDevFile, priorBytes);
        string priorDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(priorBytes)));
        SquadReceipt? receipt = _state.Store.ReadReceipt(tempHome, SquadDeploymentScope.Global);
        Assert.NotNull(receipt);
        List<SquadOwnedFile> updatedFiles = receipt.Files.Select(f =>
            f.RelativePath == "agents/csharp-dev.md" && f.Target == "cursor"
                ? f with { Sha256 = priorDigest }
                : f).ToList();
        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(tempHome, SquadDeploymentScope.Global),
            _state.Store.SerializeReceipt(receipt with { Files = updatedFiles }));

        int exitCode = Run(
            executor,
            repoPath,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            },
            globalRoots);

        string normalizedOutput = Normalize(Output());
        Assert.Equal(0, exitCode);
        Assert.Contains("Global unmanaged collisions: none", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warn Unmanaged global file", Output(), StringComparison.Ordinal);

        // A genuine unmanaged colliding file (not recorded in receipt) is warned
        string unmanagedFile = Path.Combine(tempHome, ".pi", "agent", "agents", "architect.md");
        WriteFile(unmanagedFile, "unmanaged-architect-content");

        int unmanagedExitCode = Run(
            executor,
            repoPath,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            },
            globalRoots);

        string unmanagedOutput = Normalize(Output());
        Assert.Contains("warn", unmanagedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agents/architect.md", unmanagedOutput.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.DoesNotContain("csharp-dev", unmanagedOutput, StringComparison.Ordinal);
        Assert.Equal(0, unmanagedExitCode);
    }

    [Fact]
    public void Doctor_GlobalScope_CorruptSiblingReceipt_DoesNotCrash()
    {
        string tempHome = CreateDirectory("doctor-corrupt-sibling-home");
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        string repoPath = CreateCanonicalRepository("doctor-corrupt-sibling-repository");

        string siblingRepo = CreateDirectory("doctor-corrupt-sibling-repo");
        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(siblingRepo, SquadDeploymentScope.Global),
            "{ this is corrupt json \n");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(
            executor,
            repoPath,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            },
            globalRoots);

        Assert.Equal(1, exitCode);
        Assert.Contains("Global unmanaged-collision scan failed", Output(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doctor found issues", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Doctor_GlobalScope_BlankSiblingReceipt_DoesNotCrash(string blankReceipt)
    {
        string tempHome = CreateDirectory($"doctor-blank-sibling-home-{blankReceipt.Length}");
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        string repoPath = CreateCanonicalRepository($"doctor-blank-sibling-repository-{blankReceipt.Length}");

        string siblingRepo = CreateDirectory($"doctor-blank-sibling-repo-{blankReceipt.Length}");
        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(siblingRepo, SquadDeploymentScope.Global),
            blankReceipt);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(
            executor,
            repoPath,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            },
            globalRoots);

        Assert.Equal(1, exitCode);
        Assert.Contains("Global unmanaged-collision scan failed", Output(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doctor found issues", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_GlobalScope_CorruptLocalReceipt_DoesNotCrash()
    {
        string tempHome = CreateDirectory("doctor-corrupt-local-home");
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        string repoPath = CreateCanonicalRepository("doctor-corrupt-local-repository");

        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(repoPath, SquadDeploymentScope.Global),
            "{ corrupt local json \n");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        int exitCode = Run(
            executor,
            repoPath,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            },
            globalRoots);

        Assert.Equal(1, exitCode);
        Assert.Contains("Global unmanaged-collision scan failed", Output(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doctor found issues", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_GlobalScope_StillRefusesUnmanagedCollision()
    {
        string tempHome = CreateDirectory("install-global-collision-home");
        string projectRoot = CreateDirectory("install-global-collision-project");
        WriteFile(
            Path.Combine(tempHome, ".pi", "agent", "agents", "architect.md"),
            "not-the-canonical-architect-bytes");

        SquadGlobalRoots globalRoots = new(_ => null, tempHome, isWindows: false);
        string repoPath = CreateCanonicalRepository("install-global-collision-repository");
        SquadLifecycleService service = InMemoryLifecycleOverShippedSource(repoPath, globalRoots);

        SquadDeploymentConflictException exception = await Assert.ThrowsAsync<SquadDeploymentConflictException>(
            () => service.InstallAsync(new SquadInstallRequest(
                TargetRoot: projectRoot,
                Scope: SquadDeploymentScope.Global,
                Targets: [SquadTarget.Pi],
                Version: "1.2.3")));

        Assert.Contains("Unmanaged path", exception.Message, StringComparison.Ordinal);
        Assert.Contains("agents/architect.md", exception.Message, StringComparison.Ordinal);
    }

    private int Run(
        FakeProcessExecutor executor,
        string workingDirectory,
        SquadDoctorSettings settings,
        ISquadGlobalRootResolver? globalRoots = null) =>
        SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            workingDirectory,
            globalRoots,
            output: _console).Execute(null!, settings);

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Normalize(string output) =>
        string.Join(' ', output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ExtractDoctorSection(string output, string header, params string[] otherHeaders)
    {
        int start = output.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, $"Doctor output is missing the '{header}' section.");

        int end = output.Length;
        foreach (string otherHeader in otherHeaders)
        {
            int candidate = output.IndexOf(otherHeader, start + header.Length, StringComparison.OrdinalIgnoreCase);
            if (candidate >= 0 && candidate < end)
            {
                end = candidate;
            }
        }

        return output[start..end];
    }

    private string CreateDirectory(string name)
    {
        string path = Path.Combine(_state.Files.Root, name);
        _state.Files.CreateDirectory(path);
        return _state.Layout.PhysicalOf(path);
    }

    private void WriteFile(string path, string content)
    {
        _state.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _state.Files.WriteAllText(path, content);
    }

    private string CreateCanonicalRepository(string name)
    {
        string repo = CreateDirectory(name);
        _state.Files.WriteAllText(
            Path.Combine(repo, "KyberWeave.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00");
        CanonicalSquadTree.CopyInto(_state.Files, Path.Combine(repo, "products", "kyber-squad"));
        return repo;
    }

    private SquadLifecycleService InMemoryLifecycleOverShippedSource(
        string repoPath,
        ISquadGlobalRootResolver globalRoots)
    {
        FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(
            Path.Combine(repoPath, "products", "kyber-squad"),
            _state.Files);
        ISquadRenderer renderer = new SquadRendererRegistry(
            SquadHostDefaults.CreateRenderers(),
            new InMemorySquadServices(_state.Files).RenderInputReader());
        return SquadTestCommands.InMemoryLifecycle(_state, release, renderer, globalRoots);
    }

    private async Task InstallGlobalCursorAsync(
        string repoPath,
        string tempHome,
        ISquadGlobalRootResolver globalRoots)
    {
        SquadLifecycleService service = InMemoryLifecycleOverShippedSource(repoPath, globalRoots);
        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: tempHome,
            Scope: SquadDeploymentScope.Global,
            Targets: [SquadTarget.Cursor],
            Version: "1.2.3"));
        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
    }
}
