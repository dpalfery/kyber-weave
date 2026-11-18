using KyberWeave.Testing.Squad;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Task K6 test suite: Pins the Kyber-Squad CLI command surface, settings, execution semantics,
/// and source discovery boundaries before toolchain qualification and runtime rendering.
/// Covers Test Contract K6 (K6a, K6b, K6c, K6d) from docs/plans/2026-08-14-kyber-squad-unified-agent-skill-deployment.md.
/// </summary>
public sealed class SquadCliCommandTests : IDisposable
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>
    /// This class's prepared installs, built on the first constructor call so no test body runs one.
    /// </summary>
    private static readonly Lazy<PreparedCases> Prepared = new(BuildPreparedCases);

    private readonly TempDirectory _temp = new();

    /// <summary>Builds every prepared install before the first test body, so a test only reads a record or a copy.</summary>
    public SquadCliCommandTests()
    {
        _ = Prepared.Value;
    }

    public void Dispose() => _temp.Dispose();

    #region SquadPackSourceLocator Tests (K6a, K6b, K6c)

    [Fact]
    public void Pack_OutsideRepositoryRoot_FailsWithMaintainerGuidanceBeforeSideEffects()
    {
        // K6a: Verifies locator returns null when KyberWeave.sln or products/kyber-squad/squad.yml
        // is missing in the immediate working directory.
        string emptyDir = Path.Combine(_temp.Path, "empty-dir");
        Directory.CreateDirectory(emptyDir);
        Assert.Null(SquadPackSourceLocator.Resolve(emptyDir));

        // Missing squad.yml only
        string slnOnlyDir = Path.Combine(_temp.Path, "sln-only");
        Directory.CreateDirectory(slnOnlyDir);
        File.WriteAllText(Path.Combine(slnOnlyDir, "KyberWeave.sln"), "Microsoft Visual Studio Solution File");
        Assert.Null(SquadPackSourceLocator.Resolve(slnOnlyDir));

        // Missing KyberWeave.sln only
        string squadYmlOnlyDir = Path.Combine(_temp.Path, "squad-yml-only");
        Directory.CreateDirectory(Path.Combine(squadYmlOnlyDir, "products", "kyber-squad"));
        File.WriteAllText(Path.Combine(squadYmlOnlyDir, "products", "kyber-squad", "squad.yml"), "schema: kyber-squad.squad/v1");
        Assert.Null(SquadPackSourceLocator.Resolve(squadYmlOnlyDir));

        // Directory exists but squad.yml file is absent
        string dirWithoutSquadYml = Path.Combine(_temp.Path, "dir-without-squad-yml");
        Directory.CreateDirectory(Path.Combine(dirWithoutSquadYml, "products", "kyber-squad"));
        File.WriteAllText(Path.Combine(dirWithoutSquadYml, "KyberWeave.sln"), "solution");
        Assert.Null(SquadPackSourceLocator.Resolve(dirWithoutSquadYml));
    }

    [Fact]
    public void Pack_DoesNotSearchParentDownloadOrUseEmbeddedSource()
    {
        // K6b: Verifies locator does not climb parents or fall back to embedded resources.
        using SquadRepoFixture repo = SquadRepoFixture.CreateValid();

        // Run from child directory beneath a valid checkout
        string childDir = Path.Combine(repo.Path, "src", "KyberWeave.Cli");
        Directory.CreateDirectory(childDir);

        Assert.Null(SquadPackSourceLocator.Resolve(childDir));

        // Assert CLI, Core and Squad assemblies have no embedded resource for products/kyber-squad corpus
        Assembly cliAssembly = typeof(KyberWeave.Cli.CliApplication).Assembly;
        Assembly coreAssembly = typeof(KyberWeave.Core.Diagnostics.DiagnosticReport).Assembly;
        Assembly squadAssembly = typeof(SquadSource).Assembly;

        string[] cliResources = cliAssembly.GetManifestResourceNames();
        string[] coreResources = coreAssembly.GetManifestResourceNames();
        string[] squadResources = squadAssembly.GetManifestResourceNames();

        Assert.DoesNotContain(cliResources, name => name.Contains("products", StringComparison.OrdinalIgnoreCase) ||
                                                    name.Contains("kyber-squad", StringComparison.OrdinalIgnoreCase) ||
                                                    name.EndsWith("squad.yml", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(coreResources, name => name.Contains("products", StringComparison.OrdinalIgnoreCase) ||
                                                     name.Contains("kyber-squad", StringComparison.OrdinalIgnoreCase) ||
                                                     name.EndsWith("squad.yml", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(squadResources, name => name.Contains("products", StringComparison.OrdinalIgnoreCase) ||
                                                      name.Contains("kyber-squad", StringComparison.OrdinalIgnoreCase) ||
                                                      name.EndsWith("squad.yml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_RepositoryRoot_UsesTrackedCanonicalSource()
    {
        // K6c locator: Verifies locator succeeds when both markers exist in immediate directory.
        using SquadRepoFixture repo = SquadRepoFixture.CreateValid();

        string? resolved = SquadPackSourceLocator.Resolve(repo.Path);

        Assert.NotNull(resolved);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(repo.Path, "products", "kyber-squad")),
            Path.GetFullPath(resolved));
    }

    #endregion

    #region SquadPackCommand Execution Tests (K6a, K6c)

    [Theory]
    [InlineData("apm")]
    [InlineData("plugins")]
    [InlineData("all")]
    public void Pack_OutsideRepositoryRoot_ExitsNonZeroWithGuidanceAndNoSideEffects(string format)
    {
        // K6a CLI execution: execute every format from a temporary directory containing neither marker.
        // Assert exit 1; escaped output names KyberWeave.sln, products/kyber-squad/squad.yml, and squad install;
        // --out remains absent/unchanged; network and process fakes receive zero calls.
        string workingDir = Path.Combine(_temp.Path, "outside-root");
        Directory.CreateDirectory(workingDir);
        string outDir = Path.Combine(_temp.Path, "pack-out");

        FakeProcessExecutor executor = new FakeProcessExecutor();
        SquadPackCommand command = SquadTestCommands.Pack(workingDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = format,
                Out = outDir
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KyberWeave.sln", execution.Output, StringComparison.Ordinal);
        Assert.Contains("products/kyber-squad/squad.yml", execution.Output, StringComparison.Ordinal);
        Assert.Contains("squad install", execution.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outDir));
        Assert.Empty(executor.Calls);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("zip")]
    [InlineData("tar")]
    [InlineData("other")]
    public void Pack_InvalidFormatToken_ExitsTwo(string format)
    {
        FakeProcessExecutor executor = new FakeProcessExecutor();
        SquadPackCommand command = SquadTestCommands.Pack(_temp.Path);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = format,
                Out = Path.Combine(_temp.Path, "out")
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Invalid pack format", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pack_RepositoryRoot_UsesTrackedCanonicalSourceAndSucceeds()
    {
        // K6c CLI execution: run at a root containing both markers and valid canonical
        // source. Packing writes the canonical tree deterministically with no external
        // toolchain in the loop, so a valid source root packs successfully outright —
        // there is no longer a toolchain-qualification gate to reach.
        using SquadRepoFixture repo = SquadRepoFixture.CreateValid();
        string repoPath = repo.Path;
        string outDir = Path.Combine(_temp.Path, "pack-output");

        FakeProcessExecutor executor = new FakeProcessExecutor();
        SquadPackCommand command = SquadTestCommands.Pack(repoPath);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = "all",
                Out = outDir
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.True(Directory.Exists(outDir));
        Assert.NotEmpty(Directory.GetFiles(outDir, "kyber-squad-*.zip"));
        Assert.NotEmpty(Directory.GetFiles(outDir, "kyber-squad-plugin-*.zip"));
        Assert.True(File.Exists(Path.Combine(outDir, "SHA256SUMS.txt")));
        Assert.Empty(executor.Calls);
    }

    #endregion

    #region SquadStatusCommand Execution Tests

    [Fact]
    public void Status_MissingReceipt_ExitsOneWithGuidanceToInstall()
    {
        string targetDir = Path.Combine(_temp.Path, "no-receipt-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStatusCommand command = SquadTestCommands.Status(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("squad install", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ValidReceiptWithMatchingHashes_ExitsZero()
    {
        string targetDir = Path.Combine(_temp.Path, "valid-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        SquadStatusCommand command = SquadTestCommands.Status(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("architect.md", execution.Output, StringComparison.Ordinal);
        Assert.Contains("test-dev", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ModifiedFile_ExitsOneAndIdentifiesDrift()
    {
        string targetDir = Path.Combine(_temp.Path, "drifted-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        // Modify one file on disk to simulate drift
        string driftedFilePath = Path.Combine(targetDir, "agents", "architect.md");
        File.WriteAllText(driftedFilePath, "Locally modified content.");

        SquadStatusCommand command = SquadTestCommands.Status(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("agents/architect.md", execution.Output, StringComparison.Ordinal);
        Assert.Contains("modified locally", execution.Output, StringComparison.Ordinal);
        Assert.Contains("--replace-managed", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_MissingFile_ExitsOneAndIdentifiesMissingFile()
    {
        string targetDir = Path.Combine(_temp.Path, "missing-file-project");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        // Delete one file
        File.Delete(Path.Combine(targetDir, "skills", "test-dev", "SKILL.md"));

        SquadStatusCommand command = SquadTestCommands.Status(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("skills/test-dev/SKILL.md", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_GlobalFlag_RoutesToGlobalStateStore()
    {
        string targetDir = Path.Combine(_temp.Path, "project-without-receipt");
        Directory.CreateDirectory(targetDir);

        string globalRoot = Path.Combine(_temp.Path, "global-target");
        Directory.CreateDirectory(globalRoot);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(globalRoot, SquadDeploymentScope.Global, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        // A global receipt's paths resolve beneath each target's own global root, so the
        // seeded deployment is only verifiable through a resolver that maps codex there.
        SquadStatusCommand command = SquadTestCommands.Status(
            userPaths,
            stateStore,
            new FakeGlobalRootResolver(SquadTarget.Codex, globalRoot));

        // Project status should exit 1 because project has no receipt
        CommandExecution projectExecution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));
        Assert.Equal(1, projectExecution.ExitCode);

        // Global status routes to global state store and finds the seeded global receipt
        CommandExecution globalExecution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = globalRoot,
                Global = true
            }));
        Assert.Equal(0, globalExecution.ExitCode);
        Assert.Contains("architect.md", globalExecution.Output, StringComparison.Ordinal);
    }

    #endregion

    #region SquadDoctorCommand Execution Tests (K6d)

    [Fact]
    public void Doctor_SourceLessInstalledLayout_DoesNotReportMissingCanonicalSource()
    {
        // K6d: Run doctor beside a publish-layout fixture with no repository markers.
        // It may report genuine release/state/tool failures, but it does not report missing products/kyber-squad
        // as an installation defect and never attempts pack/source discovery.
        string installedDir = Path.Combine(_temp.Path, "sourceless-installed-layout");
        Directory.CreateDirectory(installedDir);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("apm", "apm, version 0.28.0\n")
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = SquadTestCommands.Doctor(executor, userPaths, workingDirectory: installedDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = installedDir,
                Global = false
            }));

        Assert.DoesNotContain("products/kyber-squad", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing canonical source", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_ReportsRendererCoverageAndMcpProbeStatus()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-workdir");
        Directory.CreateDirectory(workingDir);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = SquadTestCommands.Doctor(executor, userPaths, workingDirectory: workingDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = workingDir,
                Global = false
            }));

        // Doctor has separate status sections. Normalize whitespace because Spectre.Console
        // can wrap headers at the console width, then inspect each section independently so
        // the test does not depend on section ordering.
        string normalizedOutput = string.Join(
            ' ',
            execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
        string workingDir = Path.Combine(_temp.Path, "doctor-devin-overlap");
        foreach (string root in new[] { ".devin", ".claude" })
        {
            string skillDirectory = Path.Combine(workingDir, root, "skills", "code-review");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: code-review\n---\n");
        }

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadGlobalRoots globalRoots = new(_ => null, Path.Combine(_temp.Path, "doctor-devin-home"), isWindows: false);
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            userPaths,
            workingDirectory: workingDir,
            globalRoots: globalRoots);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = workingDir,
                Global = false
            }));

        string normalizedOutput = string.Join(
            ' ',
            execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("warn", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".claude/skills/", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("code-review", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("\"claude\": false", normalizedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WorkspaceWithoutDevin_SkipsTheDuplicateImportCheck()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-no-devin");
        Directory.CreateDirectory(workingDir);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = SquadTestCommands.Doctor(executor, userPaths, workingDirectory: workingDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = workingDir,
                Global = false
            }));

        string normalizedOutput = string.Join(
            ' ',
            execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Devin duplicate imports: not checked", normalizedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WhenMcpProbeFails_ReportsFailureReason()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-missing-mcp");
        Directory.CreateDirectory(workingDir);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithFailure("kyber-weave-mcp", "The 'kyber-weave-mcp' executable is not available on PATH.");

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = SquadTestCommands.Doctor(executor, userPaths, workingDirectory: workingDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = workingDir,
                Global = false
            }));

        // Two assertions rather than one contiguous phrase: Spectre.Console wraps
        // MarkupLine output at the console width, and "kyber-weave-mcp ... not
        // available on PATH." is long enough to wrap mid-phrase.
        Assert.Contains("kyber-weave-mcp", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not available on", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WhenInsideRepository_ValidatesLocalSource()
    {
        using SquadRepoFixture repo = SquadRepoFixture.CreateValid();
        string repoPath = repo.Path;

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("apm", "apm, version 0.28.0\n")
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = SquadTestCommands.Doctor(executor, userPaths, workingDirectory: repoPath);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = false
            }));

        // Inside repo root, doctor validates canonical source
        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("canonical source", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_GlobalScope_WarnsOnUnmanagedNameCollidingFile()
    {
        string tempHome = Path.Combine(_temp.Path, "doctor-global-home");
        string collidingRelative = Path.Combine("agents", "architect.md");
        string collidingFull = Path.Combine(tempHome, ".pi", "agent", collidingRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(collidingFull)!);
        File.WriteAllText(collidingFull, "not-the-canonical-architect-bytes");

        string factoryCollidingFull = Path.Combine(tempHome, ".factory", "droids", "architect.md");
        Directory.CreateDirectory(Path.GetDirectoryName(factoryCollidingFull)!);
        File.WriteAllText(factoryCollidingFull, "not-the-canonical-factory-architect-bytes");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            userPaths,
            workingDirectory: Corpus.Root,
            globalRoots: globalRoots);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = Corpus.Root,
                Global = true
            }));

        string normalizedOutput = string.Join(
            ' ',
            execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("warn", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("architect", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("agents/architect.md", normalizedOutput.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("canonical identity", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no verified global root", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("droids/architect.md", normalizedOutput.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_GlobalScope_ReportsRelativeOverrideAndExitsNonZero()
    {
        string tempHome = Path.Combine(_temp.Path, "doctor-relative-override-home");
        Directory.CreateDirectory(tempHome);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "doctor-relative-user"));
        SquadGlobalRoots globalRoots = new(
            name => string.Equals(name, "CLAUDE_CONFIG_DIR", StringComparison.Ordinal)
                ? "relative-dir"
                : null,
            tempHome);
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            userPaths,
            workingDirectory: Corpus.Root,
            globalRoots: globalRoots);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = Corpus.Root,
                Global = true
            }));

        string normalizedOutput = string.Join(
            ' ',
            execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("Invalid global root", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fully qualified", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Global unmanaged collisions", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doctor found issues", normalizedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_GlobalScope_SkipsCollisionScanWhenCanonicalSourceIsInvalid()
    {
        string repoPath = Path.Combine(_temp.Path, "invalid-squad-repo");
        Directory.CreateDirectory(Path.Combine(repoPath, "products", "kyber-squad"));
        File.WriteAllText(Path.Combine(repoPath, "KyberWeave.sln"), string.Empty);
        File.WriteAllText(
            Path.Combine(repoPath, "products", "kyber-squad", "squad.yml"),
            "this is not a valid squad manifest\n");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "invalid-source-user"));
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            userPaths,
            workingDirectory: repoPath,
            globalRoots: new SquadGlobalRoots(_ => null, Path.Combine(_temp.Path, "invalid-source-home")));

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = repoPath,
                Global = true
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("Canonical source", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scan skipped", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Global unmanaged collisions: none", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_GlobalScope_CleanInstall_ReportsNoUnmanagedCollisions()
    {
        PreparedGlobalInstall prepared = Prepared.Value.GlobalCursorInstall;
        Assert.Equal(0, prepared.Record.ExitCode);

        // The install itself is not this test's subject: it needs a clean global deployment of Cursor to
        // edit, so it copies the run-wide prepared install into its own home and re-roots it there.
        string tempHome = Path.Combine(_temp.Path, "doctor-clean-install-home");
        string userHome = Path.Combine(_temp.Path, "doctor-clean-install-user");
        FakeSquadUserPaths userPaths = new(userHome);
        prepared.CopyInto(tempHome, userPaths);
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        // Simulate a file recorded in the receipt whose bytes differ from the local source render
        // (the authoritative repro in issue #283: receipt-managed file flagged as unmanaged by doctor)
        string csharpDevFile = Path.Combine(tempHome, ".cursor", "agents", "csharp-dev.md");
        string priorBytes = "prior-release-csharp-dev-content";
        await File.WriteAllTextAsync(csharpDevFile, priorBytes);
        string priorDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(priorBytes)));
        SquadReceipt? receipt = stateStore.ReadReceipt(Corpus.Root, SquadDeploymentScope.Global);
        Assert.NotNull(receipt);
        List<SquadOwnedFile> updatedFiles = receipt.Files.Select(f =>
            f.RelativePath == "agents/csharp-dev.md" && f.Target == "cursor"
                ? f with { Sha256 = priorDigest }
                : f).ToList();
        string receiptPath = stateStore.ResolveReceiptPath(Corpus.Root, SquadDeploymentScope.Global);
        await File.WriteAllTextAsync(receiptPath, stateStore.SerializeReceipt(receipt with { Files = updatedFiles }));

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n")
            .WithMatchingArbiterProbe();
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            userPaths,
            workingDirectory: Corpus.Root,
            globalRoots: globalRoots);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = Corpus.Root,
                Global = true
            }));

        string normalizedOutput = string.Join(
            ' ',
            execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Global unmanaged collisions: none", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warn Unmanaged global file", execution.Output, StringComparison.Ordinal);

        // A genuine unmanaged colliding file (not recorded in receipt) is warned
        string unmanagedFile = Path.Combine(tempHome, ".pi", "agent", "agents", "architect.md");
        Directory.CreateDirectory(Path.GetDirectoryName(unmanagedFile)!);
        await File.WriteAllTextAsync(unmanagedFile, "unmanaged-architect-content");

        CommandExecution unmanagedExecution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = Corpus.Root,
                Global = true
            }));

        string unmanagedOutput = string.Join(
            ' ',
            unmanagedExecution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("warn", unmanagedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agents/architect.md", unmanagedOutput.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.DoesNotContain("csharp-dev", unmanagedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_GlobalScope_CorruptSiblingReceipt_DoesNotCrash()
    {
        string tempHome = Path.Combine(_temp.Path, "doctor-corrupt-sibling-home");
        Directory.CreateDirectory(tempHome);
        string userHome = Path.Combine(_temp.Path, "doctor-corrupt-sibling-user");
        FakeSquadUserPaths userPaths = new(userHome);
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        string siblingRepo = Path.Combine(_temp.Path, "doctor-corrupt-sibling-repo");
        Directory.CreateDirectory(siblingRepo);
        string siblingReceiptPath = stateStore.ResolveReceiptPath(siblingRepo, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingReceiptPath)!);
        File.WriteAllText(siblingReceiptPath, "{ this is corrupt json \n");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            userPaths,
            workingDirectory: Corpus.Root,
            globalRoots: globalRoots,
            stateStore: stateStore);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = Corpus.Root,
                Global = true
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("Global unmanaged-collision scan failed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doctor found issues", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Doctor_GlobalScope_BlankSiblingReceipt_DoesNotCrash(string blankReceipt)
    {
        string tempHome = Path.Combine(_temp.Path, $"doctor-blank-sibling-home-{blankReceipt.Length}");
        Directory.CreateDirectory(tempHome);
        string userHome = Path.Combine(_temp.Path, $"doctor-blank-sibling-user-{blankReceipt.Length}");
        FakeSquadUserPaths userPaths = new(userHome);
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        string siblingRepo = Path.Combine(_temp.Path, $"doctor-blank-sibling-repo-{blankReceipt.Length}");
        Directory.CreateDirectory(siblingRepo);
        string siblingReceiptPath = stateStore.ResolveReceiptPath(siblingRepo, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingReceiptPath)!);
        File.WriteAllText(siblingReceiptPath, blankReceipt);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            userPaths,
            workingDirectory: Corpus.Root,
            globalRoots: globalRoots,
            stateStore: stateStore);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = Corpus.Root,
                Global = true
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("Global unmanaged-collision scan failed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doctor found issues", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_GlobalScope_CorruptLocalReceipt_DoesNotCrash()
    {
        string tempHome = Path.Combine(_temp.Path, "doctor-corrupt-local-home");
        Directory.CreateDirectory(tempHome);
        string userHome = Path.Combine(_temp.Path, "doctor-corrupt-local-user");
        FakeSquadUserPaths userPaths = new(userHome);
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        string receiptPath = stateStore.ResolveReceiptPath(Corpus.Root, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, "{ corrupt local json \n");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.Doctor(
            executor,
            userPaths,
            workingDirectory: Corpus.Root,
            globalRoots: globalRoots,
            stateStore: stateStore);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = Corpus.Root,
                Global = true
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("Global unmanaged-collision scan failed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doctor found issues", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]

    public async Task Install_GlobalScope_StillRefusesUnmanagedCollision()
    {
        string tempHome = Path.Combine(_temp.Path, "install-global-collision-home");
        string projectRoot = Path.Combine(_temp.Path, "install-global-collision-project");
        Directory.CreateDirectory(projectRoot);
        string collidingFull = Path.Combine(tempHome, ".pi", "agent", "agents", "architect.md");
        Directory.CreateDirectory(Path.GetDirectoryName(collidingFull)!);
        await File.WriteAllTextAsync(collidingFull, "not-the-canonical-architect-bytes");

        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "install-global-user"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(
            releaseSource,
            SquadHostDefaults.ResolveRenderer(),
            stateStore,
            globalRoots: globalRoots);

        SquadDeploymentConflictException exception = await Assert.ThrowsAsync<SquadDeploymentConflictException>(
            () => service.InstallAsync(new SquadInstallRequest(
                TargetRoot: projectRoot,
                Scope: SquadDeploymentScope.Global,
                Targets: [SquadTarget.Pi],
                Version: "1.2.3")));

        Assert.Contains("Unmanaged path", exception.Message, StringComparison.Ordinal);
        Assert.Contains("agents/architect.md", exception.Message, StringComparison.Ordinal);
    }

    #endregion

    #region SquadUninstallCommand Execution Tests

    [Fact]
    public void Uninstall_MissingReceipt_ExitsZeroAsNoOp()
    {
        string targetDir = Path.Combine(_temp.Path, "no-receipt-uninstall");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                PathOption = targetDir,
                Global = false,
                DryRun = false
            }));

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void Uninstall_ExistingReceipt_ExecutesTransactionAndRemovesFilesAndState()
    {
        string targetDir = Path.Combine(_temp.Path, "uninstall-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        string deployedFile1 = Path.Combine(targetDir, "agents", "architect.md");
        string deployedFile2 = Path.Combine(targetDir, "skills", "test-dev", "SKILL.md");
        string receiptPath = Path.Combine(HomeProjectStateDirectory(userPaths, targetDir), "squad.receipt.json");
        string lockPath = Path.Combine(HomeProjectStateDirectory(userPaths, targetDir), "squad.lock.yml");

        Assert.True(File.Exists(deployedFile1));
        Assert.True(File.Exists(deployedFile2));
        Assert.True(File.Exists(receiptPath));
        Assert.True(File.Exists(lockPath));

        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                PathOption = targetDir,
                Global = false,
                DryRun = false
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.False(File.Exists(deployedFile1));
        Assert.False(File.Exists(deployedFile2));
        Assert.False(File.Exists(receiptPath));
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Uninstall_DryRun_PreservesFilesAndState()
    {
        string targetDir = Path.Combine(_temp.Path, "uninstall-dryrun-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        string deployedFile1 = Path.Combine(targetDir, "agents", "architect.md");
        string receiptPath = Path.Combine(HomeProjectStateDirectory(userPaths, targetDir), "squad.receipt.json");

        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                PathOption = targetDir,
                Global = false,
                DryRun = true
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.True(File.Exists(deployedFile1));
        Assert.True(File.Exists(receiptPath));
    }

    [Fact]
    public void Uninstall_WhenCorruptReceipt_ExitsOne()
    {
        string targetDir = Path.Combine(_temp.Path, "corrupt-receipt-uninstall");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        string receiptPath = stateStore.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, "invalid json {[[");

        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths, stateStore);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                PathOption = targetDir,
                Global = false,
                DryRun = false
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uninstall_NamedHarness_RemovesOnlyThatHarnessUntilTheLastOneGoes()
    {
        (string targetDir, FakeSquadUserPaths userPaths, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-one-harness");
        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths);

        CommandExecution first = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings { Harness = "cursor", Project = true, PathOption = targetDir }));

        Assert.Equal(0, first.ExitCode);
        Assert.False(File.Exists(cursorFile));
        Assert.True(File.Exists(codexFile));
        Assert.True(File.Exists(receiptPath));
        SquadReceipt? remaining = RealSquadState.Store(userPaths).ReadReceipt(targetDir, SquadDeploymentScope.Project);
        Assert.Equal(["codex"], remaining!.Files.Select(file => file.Target).Distinct());

        CommandExecution last = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings { Harness = "codex", Project = true, PathOption = targetDir }));

        Assert.Equal(0, last.ExitCode);
        Assert.False(File.Exists(codexFile));
        Assert.False(File.Exists(receiptPath));
    }

    [Fact]
    public void Uninstall_NamedHarnessDryRun_PreservesFilesAndReceipt()
    {
        (string targetDir, FakeSquadUserPaths userPaths, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-one-harness-dry-run");

        CommandExecution execution = Capture(() => SquadTestCommands.Uninstall(userPaths).Execute(
            null!,
            new SquadUninstallSettings { Harness = "cursor", Project = true, PathOption = targetDir, DryRun = true }));

        Assert.Equal(0, execution.ExitCode);
        Assert.True(File.Exists(codexFile));
        Assert.True(File.Exists(cursorFile));
        Assert.True(File.Exists(receiptPath));
    }

    [Fact]
    public void Uninstall_InvalidHarnessInput_ExitsTwoWithoutChanges()
    {
        (string targetDir, FakeSquadUserPaths userPaths, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-invalid-harness-input");
        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths);

        (SquadUninstallSettings Settings, string ExpectedMessage)[] invalid =
        [
            (new() { Harness = "nope", Project = true, PathOption = targetDir }, "nope"),
            (new() { Harness = "all", Project = true, PathOption = targetDir }, "exactly one harness"),
            (new() { Harness = "claude", Project = true, PathOption = targetDir }, "'claude' is not installed"),
            (new() { Harness = "cursor", Project = true, Global = true, PathOption = targetDir }, "mutually exclusive"),
            (new() { Harness = "cursor", PathOption = targetDir }, "--path is only valid with --project"),
            (new() { Harness = "cursor" }, "'cursor' is not installed in the global deployment")
        ];

        foreach ((SquadUninstallSettings settings, string expectedMessage) in invalid)
        {
            CommandExecution execution = Capture(() => command.Execute(null!, settings));
            Assert.Equal(2, execution.ExitCode);
            Assert.Contains(expectedMessage, execution.Output, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(File.Exists(codexFile));
        Assert.True(File.Exists(cursorFile));
        Assert.True(File.Exists(receiptPath));
    }

    [Fact]
    public void Uninstall_NamedHarnessWithoutProject_UsesTheGlobalScopeAndKeepsTheOtherHarness()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "uninstall-global-harness"));
        string home = userPaths.HomeDirectory;
        Directory.CreateDirectory(home);
        SquadGlobalRoots roots = new(_ => null, home);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        SquadLock squadLock = new(
            "kyber-squad.lock/v1",
            "1.2.3",
            "1.2.3",
            "1.2.3",
            "full",
            ["codex", "cursor"],
            [],
            "best-effort",
            "a".PadRight(64, '0'),
            "b".PadRight(64, '0'),
            new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));
        new SquadTransaction(stateStore).Execute(SquadDeploymentPlanner.CreateInstall(
            home,
            SquadDeploymentScope.Global,
            squadLock,
            [
                new SquadDeploymentFile("agents/architect.md", "You are architect.\n"u8.ToArray(), "codex"),
                new SquadDeploymentFile("agents/reviewer.md", "You are reviewer.\n"u8.ToArray(), "cursor")
            ],
            [],
            adopt: false,
            TimeProvider.System,
            globalRoots: roots));
        string codexFile = Path.Combine(roots.ResolveGlobalRoot(SquadTarget.Codex), "agents", "architect.md");
        string cursorFile = Path.Combine(roots.ResolveGlobalRoot(SquadTarget.Cursor), "agents", "reviewer.md");
        Assert.True(File.Exists(codexFile));
        Assert.True(File.Exists(cursorFile));
        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths, stateStore, null, false, null, roots);

        CommandExecution first = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings { Harness = "cursor", Yes = true }));

        Assert.Equal(0, first.ExitCode);
        Assert.False(File.Exists(cursorFile));
        Assert.True(File.Exists(codexFile));
        SquadReceipt remaining = Assert.IsType<SquadReceipt>(stateStore.ReadReceipt(home, SquadDeploymentScope.Global));
        Assert.Equal(["codex"], remaining.Files.Select(file => file.Target).Distinct());

        CommandExecution last = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings { Harness = "codex", Yes = true }));

        Assert.Equal(0, last.ExitCode);
        Assert.False(File.Exists(codexFile));
        Assert.Null(stateStore.ReadReceipt(home, SquadDeploymentScope.Global));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./deploy")]
    public void Uninstall_DirectoryGivenAsTheHarness_ExitsTwoNamingProjectAndPath(string directory)
    {
        (string targetDir, FakeSquadUserPaths userPaths, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-directory-as-harness");

        CommandExecution execution = Capture(() => SquadTestCommands.Uninstall(userPaths).Execute(
            null!,
            new SquadUninstallSettings { Harness = directory, PathOption = targetDir }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("--project", execution.Output, StringComparison.Ordinal);
        Assert.Contains("--path", execution.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(codexFile));
        Assert.True(File.Exists(cursorFile));
        Assert.True(File.Exists(receiptPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Uninstall_BlankPathOption_ExitsTwoWithoutChanges(string blank)
    {
        (string targetDir, FakeSquadUserPaths userPaths, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-blank-path");
        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths);

        foreach (SquadUninstallSettings settings in new[]
                 {
                     new SquadUninstallSettings { PathOption = blank },
                     new SquadUninstallSettings { Harness = "cursor", Project = true, PathOption = blank }
                 })
        {
            CommandExecution execution = Capture(() => command.Execute(null!, settings));

            Assert.Equal(2, execution.ExitCode);
            Assert.Contains("must not be blank", execution.Output, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(codexFile));
        Assert.True(File.Exists(cursorFile));
        Assert.True(File.Exists(receiptPath));
    }

    [Fact]
    public void Uninstall_GlobalAndProjectWithoutAHarness_ExitsTwo()
    {
        CommandExecution execution = Capture(() => SquadTestCommands.Uninstall(
            new FakeSquadUserPaths(Path.Combine(_temp.Path, "uninstall-global-and-project"))).Execute(
            null!,
            new SquadUninstallSettings { Global = true, Project = true }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("mutually exclusive", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_NamedHarnessWithCorruptReceipt_ReportsTheReadErrorAndChangesNothing()
    {
        (string targetDir, FakeSquadUserPaths userPaths, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-corrupt-receipt");
        File.WriteAllText(receiptPath, "{ not json", Encoding.UTF8);

        CommandExecution execution = Capture(() => SquadTestCommands.Uninstall(userPaths).Execute(
            null!,
            new SquadUninstallSettings { Harness = "cursor", Project = true, PathOption = targetDir }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is not installed", execution.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(codexFile));
        Assert.True(File.Exists(cursorFile));
        Assert.Equal("{ not json", File.ReadAllText(receiptPath));
    }

    private (string TargetDir, FakeSquadUserPaths UserPaths, string CodexFile, string CursorFile, string ReceiptPath)
        SeedCodexAndCursorProject(string name)
    {
        string targetDir = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(targetDir);
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        SquadReceipt receipt = stateStore.ReadReceipt(targetDir, SquadDeploymentScope.Project)!;
        File.WriteAllText(
            stateStore.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project),
            stateStore.SerializeReceipt(receipt with
            {
                Files = [.. receipt.Files.Select(file =>
                    file.RelativePath.StartsWith("skills/", StringComparison.Ordinal) ? file with { Target = "cursor" } : file)]
            }),
            Encoding.UTF8);

        return (
            targetDir,
            userPaths,
            Path.Combine(targetDir, "agents", "architect.md"),
            Path.Combine(targetDir, "skills", "test-dev", "SKILL.md"),
            stateStore.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project));
    }

    #endregion

    #region SquadInstallCommand and SquadUpdateCommand Execution Tests

    [Fact]
    public void Install_WhenToolchainUnreleased_FailsClosedWithExitOne()
    {
        PreparedCommandRun prepared = Prepared.Value.ProjectInstallUnreleasedToolchain;

        Assert.Equal(1, prepared.Record.ExitCode);
        Assert.Contains("error", prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        // Ensure no target files or state were written
        Assert.False(Directory.Exists(Path.Combine(prepared.TargetRoot, ".kyber-weave")));
    }

    [Fact]
    public void Install_WhenCollaboratorsInjected_SucceedsWithExitZero()
    {
        PreparedCommandRun prepared = Prepared.Value.ProjectInstallWithCollaborators;

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("Successfully installed", prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        string homeState = prepared.HomeStateDirectory;
        Assert.True(Directory.Exists(homeState));
        Assert.True(File.Exists(Path.Combine(homeState, "squad.lock.yml")));
        Assert.True(File.Exists(Path.Combine(homeState, "squad.receipt.json")));
    }

    [Fact]
    public void Install_DryRun_ReportsPlannedFilesWithoutModifyingFilesystem()
    {
        PreparedCommandRun prepared = Prepared.Value.ProjectDryRun;

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("Dry-run", prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(prepared.TargetRoot, ".kyber-weave")));
    }

    [Theory]
    [InlineData("invalid-target")]
    [InlineData("codex,unknown-target")]
    public void Install_InvalidTargetToken_ExitsTwo(string target)
    {
        string targetDir = Path.Combine(_temp.Path, "install-invalid-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadInstallCommand command = SquadTestCommands.Install(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = [target]
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Unknown Squad target", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invalid-exclude")]
    [InlineData("cursor,bad-exclude")]
    public void Install_InvalidExcludeToken_ExitsTwo(string exclusion)
    {
        string targetDir = Path.Combine(_temp.Path, "install-invalid-exclude");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadInstallCommand command = SquadTestCommands.Install(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["all"],
                Exclusions = [exclusion]
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Unknown Squad target", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_NoTargetInNonInteractiveTerminal_ExitsTwoWithRecoveryCommand()
    {
        string targetDir = Path.Combine(_temp.Path, "install-no-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadInstallCommand command = SquadTestCommands.Install(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = []
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("kyber-weave squad install --target <target>", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_WhenToolchainUnreleased_FailsClosedWithExitOne()
    {
        string targetDir = Path.Combine(_temp.Path, "update-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        SquadUpdateCommand command = SquadTestCommands.Update(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Global = false,
                DryRun = false,
                ReplaceManaged = false
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_WhenCollaboratorsInjected_SucceedsWithExitZero()
    {
        string targetDir = Path.Combine(_temp.Path, "update-success-target");
        Directory.CreateDirectory(targetDir);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            (".codex/agents/architect.toml", "name = \"architect\"\n"));

        SquadUpdateCommand command = SquadTestCommands.Update(
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
        Assert.Contains("Successfully updated", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Issue 99: updating a codex receipt with explicit <c>codex,claude</c> must render
    /// both harnesses and persist that same ordered set. Leaving the receipt roster in
    /// charge reports success while claude never reaches the lock or the receipt.
    /// </summary>
    [Fact]
    public void UpdateExplicitTargetsReconcileRenderedReceiptAndLockTargets()
    {
        string targetDir = Path.Combine(_temp.Path, "update-explicit-targets");
        Directory.CreateDirectory(targetDir);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home-explicit"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            (".codex/agents/architect.toml", "name = \"architect\"\n"));

        // Confirmation is a separate gate from target selection. Pin it closed so a
        // pty cannot turn this regression into a hung prompt.
        SquadUpdateCommand command = SquadTestCommands.Update(
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
                Targets = ["codex,claude"],
                Global = false,
                DryRun = false,
                ReplaceManaged = true
            }));

        Assert.Equal(0, execution.ExitCode);
        SquadRenderRequest renderRequest = Assert.Single(renderer.RenderRequests);
        SquadLock? persistedLock = stateStore.ReadLock(targetDir, SquadDeploymentScope.Project);
        SquadReceipt? persistedReceipt = stateStore.ReadReceipt(targetDir, SquadDeploymentScope.Project);
        Assert.NotNull(persistedLock);
        Assert.NotNull(persistedReceipt);
        string[] receiptTargets = persistedReceipt.Files
            .Select(file => file.Target)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Multiple(
            () => Assert.Equal([SquadTarget.Codex, SquadTarget.Claude], renderRequest.Targets),
            () => Assert.Equal(["codex", "claude"], persistedLock.Targets),
            () => Assert.Equal(["codex", "claude"], receiptTargets));
    }

    /// <summary>
    /// Issue 99: a dry-run of the same explicit pair must report the two-target deployed
    /// file count. Dry-run planning still has to leave the existing lock and receipt
    /// byte-identical, because the operator has not accepted a write.
    /// </summary>
    [Fact]
    public void UpdateExplicitTargetsDryRunReportsDesiredSetWithoutWritingState()
    {
        string targetDir = Path.Combine(_temp.Path, "update-explicit-targets-dry-run");
        Directory.CreateDirectory(targetDir);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home-explicit-dry-run"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            (".codex/agents/architect.toml", "name = \"architect\"\n"));

        string lockPath = stateStore.ResolveLockPath(targetDir, SquadDeploymentScope.Project);
        string receiptPath = stateStore.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project);
        byte[] lockBefore = File.ReadAllBytes(lockPath);
        byte[] receiptBefore = File.ReadAllBytes(receiptPath);
        int desiredFileCount = RenderedDeploymentFileCount(SquadTarget.Codex, SquadTarget.Claude);

        SquadUpdateCommand command = SquadTestCommands.Update(
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
                Targets = ["codex,claude"],
                Global = false,
                DryRun = true,
                ReplaceManaged = true
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Multiple(
            () => Assert.Equal(desiredFileCount, PlannedDeployedFileCount(execution.Output)),
            () => Assert.Equal(lockBefore, File.ReadAllBytes(lockPath)),
            () => Assert.Equal(receiptBefore, File.ReadAllBytes(receiptPath)));
    }

    [Theory]
    [InlineData("invalid-target")]
    [InlineData("claude,unknown-target")]
    public void Update_InvalidTargetToken_ExitsTwo(string target)
    {
        string targetDir = Path.Combine(_temp.Path, "update-invalid-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadUpdateCommand command = SquadTestCommands.Update(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Targets = [target]
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Unknown Squad target", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_EmptyReceiptTargetsInNonInteractiveTerminal_ExitsTwoWithRecoveryCommand()
    {
        string targetDir = Path.Combine(_temp.Path, "update-empty-receipt");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        // Seed receipt with 0 files (empty target set)
        string receiptPath = stateStore.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        SquadReceipt emptyReceipt = new SquadReceipt(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Project,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: []);
        File.WriteAllText(receiptPath, stateStore.SerializeReceipt(emptyReceipt), Encoding.UTF8);

        SquadUpdateCommand command = SquadTestCommands.Update(userPaths, stateStore);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("kyber-weave squad update --target <target>", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_MissingReceipt_ExitsOneAndDirectsToInstall()
    {
        string targetDir = Path.Combine(_temp.Path, "update-no-receipt");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadUpdateCommand command = SquadTestCommands.Update(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("squad install", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SquadCommands_DefaultConstructors_InstantiateSuccessfully()
    {
        SquadInstallCommand install = SquadTestCommands.Install();
        SquadUpdateCommand update = SquadTestCommands.Update();
        SquadUninstallCommand uninstall = SquadTestCommands.Uninstall();
        SquadStatusCommand status = SquadTestCommands.Status();
        SquadDoctorCommand doctor = SquadTestCommands.Doctor();
        SquadPackCommand pack = SquadTestCommands.Pack();

        Assert.NotNull(install);
        Assert.NotNull(update);
        Assert.NotNull(uninstall);
        Assert.NotNull(status);
        Assert.NotNull(doctor);
        Assert.NotNull(pack);
    }

    #endregion

    #region SquadCommandComposition Tests

    [Fact]
    public void SquadCommandComposition_ResolveStateStore_ReturnsConfiguredOrSharedPaths()
    {
        SquadStateStore defaultStore = SquadHostDefaults.ResolveStateStore();
        Assert.NotNull(defaultStore);

        string existingDir = _temp.Path;
        string appDataDir = Path.Combine(_temp.Path, "custom-appdata");
        FakeSquadUserPaths fakePaths = new FakeSquadUserPaths(appDataDir);
        SquadStateStore customStore = SquadHostDefaults.ResolveStateStore(fakePaths);
        Assert.NotNull(customStore);
        Assert.StartsWith(
            appDataDir,
            customStore.ResolveReceiptPath(existingDir, SquadDeploymentScope.Global),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SquadCommandComposition_ResolveProbe_ResolvesWithDefaultAndCustomExecutor()
    {
        McpProcessProbe defaultMcp = SquadCommandComposition.ResolveProbe(null, new FakeProcessEnvironment());
        Assert.NotNull(defaultMcp);

        FakeProcessExecutor executor = new FakeProcessExecutor();
        McpProcessProbe customMcp = SquadCommandComposition.ResolveProbe(executor, new FakeProcessEnvironment());
        Assert.NotNull(customMcp);
    }

    [Fact]
    public void SquadCommandComposition_ResolveTransaction_ReturnsTransactionWithResolvedState()
    {
        SquadTransaction txDefault = SquadHostDefaults.ResolveTransaction();
        Assert.NotNull(txDefault);

        FakeSquadUserPaths fakePaths = new FakeSquadUserPaths("/custom/appdata");
        SquadStateStore store = RealSquadState.Store(fakePaths);
        SquadTransaction txWithStore = SquadHostDefaults.ResolveTransaction(store);
        Assert.NotNull(txWithStore);

        SquadTransaction txWithPaths = SquadHostDefaults.ResolveTransaction(null, fakePaths);
        Assert.NotNull(txWithPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SquadCommandComposition_ResolveTargetRoot_NullOrWhitespace_ReturnsCurrentDirectoryFullPath(string? input)
    {
        string resolved = PhysicalRoots().ResolveTargetRoot(input);
        Assert.Equal(Path.GetFullPath("."), resolved);
    }

    [Fact]
    public void SquadCommandComposition_ResolveTargetRoot_SpecifiedPath_ReturnsFullPath()
    {
        string relative = "some/relative/dir";
        string resolved = PhysicalRoots().ResolveTargetRoot(relative);
        Assert.Equal(Path.GetFullPath(relative), resolved);
    }

    private static SquadCommandRoots PhysicalRoots()
    {
        PhysicalFileSystem files = new();
        return new SquadCommandRoots(
            files,
            new PhysicalPathResolver(files),
            new ProcessEnvironment(),
            new SquadConsole(Spectre.Console.AnsiConsole.Console, new SystemConsole()));
    }

    [Theory]
    [InlineData(true, SquadDeploymentScope.Global)]
    [InlineData(false, SquadDeploymentScope.Project)]
    public void SquadCommandComposition_ResolveScope_MapsCorrectly(bool isGlobal, SquadDeploymentScope expected)
    {
        Assert.Equal(expected, SquadCommandComposition.ResolveScope(isGlobal));
    }

    #endregion

    #region Program.cs Registration Tests

    [Fact]
    public void ProgramRegistersSquadBranchWithAllVerbsDescriptionsAndExamples()
    {
        string program = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "KyberWeave.Cli",
            "CliApplication.cs"));

        Assert.Contains("AddBranch(\"squad\"", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<SquadInstallCommand>(\"install\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<SquadUpdateCommand>(\"update\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<SquadUninstallCommand>(\"uninstall\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<SquadStatusCommand>(\"status\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<SquadDoctorCommand>(\"doctor\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<SquadPackCommand>(\"pack\")", program, StringComparison.Ordinal);

        // Examples
        Assert.Contains("WithExample(\"squad\", \"install\"", program, StringComparison.Ordinal);
        Assert.Contains("WithExample(\"squad\", \"update\"", program, StringComparison.Ordinal);
        Assert.Contains("WithExample(\"squad\", \"uninstall\"", program, StringComparison.Ordinal);
        Assert.Contains("WithExample(\"squad\", \"status\"", program, StringComparison.Ordinal);
        Assert.Contains("WithExample(\"squad\", \"doctor\"", program, StringComparison.Ordinal);
        Assert.Contains("WithExample(\"squad\", \"pack\"", program, StringComparison.Ordinal);

        // Strict parsing is the CLI-wide guard against silently discarded unknown
        // options (the squad --path incident): Program.cs must enable it explicitly.
        Assert.Contains("UseStrictParsing", program, StringComparison.Ordinal);
    }

    #endregion

    #region Receipt Layout Tests — Behavior (6): CLI Status and Uninstall with Legacy Receipts

    [Fact]
    public void StatusReceiptLayout_CleanLegacyGlobalReceipt_PrintsOkAndExitsZero()
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = fixture.Path;
        string appDataDir = Path.Combine(fixture.Path, "app-data");
        string fakeCodexRoot = Path.Combine(fixture.Path, "fake-codex");
        Directory.CreateDirectory(appDataDir);
        Directory.CreateDirectory(fakeCodexRoot);
        FakeSquadUserPaths userPaths = new(appDataDir);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        Directory.CreateDirectory(Path.Combine(targetRoot, ".codex", "agents"));
        File.WriteAllText(
            Path.Combine(targetRoot, ".codex", "agents", "conductor.toml"),
            "conductor content",
            Encoding.UTF8);

        SeedLegacyGlobalDeployment(targetRoot, stateStore,
            (".codex/agents/conductor.toml", "conductor content"));

        SquadGlobalRoots globalRoots = new(_ => null, Path.Combine(fixture.Path, "fake-home"));
        SquadStatusCommand command = SquadTestCommands.Status(userPaths: userPaths, globalRoots: globalRoots);
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetRoot,
                Global = true
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("ok", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legacy", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusReceiptLayout_DriftEditedFileLegacyReceipt_PrintsDriftAndExitsOne()
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = fixture.Path;
        string appDataDir = Path.Combine(fixture.Path, "app-data");
        Directory.CreateDirectory(appDataDir);
        FakeSquadUserPaths userPaths = new(appDataDir);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        Directory.CreateDirectory(Path.Combine(targetRoot, ".codex", "agents"));
        File.WriteAllText(
            Path.Combine(targetRoot, ".codex", "agents", "conductor.toml"),
            "conductor content",
            Encoding.UTF8);

        SeedLegacyGlobalDeployment(targetRoot, stateStore,
            (".codex/agents/conductor.toml", "conductor content"));

        File.WriteAllText(
            Path.Combine(targetRoot, ".codex", "agents", "conductor.toml"),
            "edited conductor content",
            Encoding.UTF8);

        SquadGlobalRoots globalRoots = new(_ => null, Path.Combine(fixture.Path, "fake-home"));
        SquadStatusCommand command = SquadTestCommands.Status(userPaths: userPaths, globalRoots: globalRoots);
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetRoot,
                Global = true
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("modified locally", execution.Output, StringComparison.Ordinal);
        Assert.Contains(".codex/agents/conductor.toml", execution.Output, StringComparison.Ordinal);
        Assert.Contains("--replace-managed", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("missing", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UninstallReceiptLayout_LegacyGlobalReceipt_ConfirmationNamesRecordedRoot()
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = fixture.Path;
        string appDataDir = Path.Combine(fixture.Path, "app-data");
        Directory.CreateDirectory(appDataDir);
        FakeSquadUserPaths userPaths = new(appDataDir);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);

        Directory.CreateDirectory(Path.Combine(targetRoot, ".codex", "agents"));
        File.WriteAllText(
            Path.Combine(targetRoot, ".codex", "agents", "conductor.toml"),
            "conductor content",
            Encoding.UTF8);

        SeedLegacyGlobalDeployment(targetRoot, stateStore,
            (".codex/agents/conductor.toml", "conductor content"));

        // This run must name the receipt's recorded root, not the resolver's per-target
        // answer. An earlier revision set CODEX_HOME process-globally to stand in for that
        // answer, but ResolveUninstallGlobalTargetRoots short-circuits the legacy branch
        // before consulting any resolver, so the variable was never read here — it only
        // raced SquadPathSafetyTests, which drives the real resolver through the same
        // override variables in a parallel test class. The injected resolver below still
        // pins the lifecycle's roots, and targetRoot is a temp directory, so the assertion
        // still fails if this branch ever does consult a resolver.
        SquadGlobalRoots globalRoots = new(_ => null, Path.Combine(fixture.Path, "fake-home"));
        using CorpusSquadReleaseSource releaseSource = new();
        SquadLifecycleService lifecycleService = SquadHostDefaults.CreateLifecycleService(releaseSource, SquadHostDefaults.ResolveRenderer(), stateStore, globalRoots: globalRoots);
        SquadUninstallCommand command = SquadTestCommands.Uninstall(userPaths: userPaths, lifecycleService: lifecycleService);
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                PathOption = targetRoot,
                Global = true,
                Yes = true
            }));

        Assert.Contains($"codex writes beneath {targetRoot}.", execution.Output, StringComparison.Ordinal);
    }

    #endregion

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

    #region Test Fixtures and Helpers

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    /// <summary>
    /// Builds every prepared install this class reads, before the first test body. Each runs the same
    /// command or lifecycle the test that reads it used to run itself, and is never written again.
    /// </summary>
    private static PreparedCases BuildPreparedCases()
    {
        const string Case = "squad-cli-command";
        string globalKey = SquadInstallRecipe.Key(
            "install",
            SquadDeploymentScope.Global,
            [SquadTarget.Cursor],
            ("case", Case),
            ("release", "corpus"),
            ("version", "1.2.3"));
        string collaboratorsKey = SquadInstallRecipe.Key(
            "install",
            SquadDeploymentScope.Project,
            [SquadTarget.Codex],
            ("case", Case),
            ("release", "fake"),
            ("renderer", "fake"),
            ("dry-run", null));
        string dryRunKey = SquadInstallRecipe.Key(
            "install",
            SquadDeploymentScope.Project,
            [SquadTarget.Codex],
            ("case", Case),
            ("release", "fake"),
            ("renderer", "fake"),
            ("dry-run", "true"));
        string unreleasedKey = SquadInstallRecipe.Key(
            "install",
            SquadDeploymentScope.Project,
            [SquadTarget.Codex],
            ("case", Case),
            ("release", "unreleased-toolchain"),
            ("dry-run", null));

        return new PreparedCases(
            PreparedSquadInstalls.Get(globalKey, () => BuildGlobalCursorInstall(globalKey)),
            PreparedSquadInstalls.Get(
                collaboratorsKey,
                () => BuildProjectInstall(collaboratorsKey, dryRun: false, unreleasedToolchain: false)),
            PreparedSquadInstalls.Get(
                dryRunKey,
                () => BuildProjectInstall(dryRunKey, dryRun: true, unreleasedToolchain: false)),
            PreparedSquadInstalls.Get(
                unreleasedKey,
                () => BuildProjectInstall(unreleasedKey, dryRun: false, unreleasedToolchain: true)));
    }

    /// <summary>The real global Cursor install the doctor collision test edits, in a home of its own.</summary>
    private static PreparedGlobalInstall BuildGlobalCursorInstall(string key)
    {
        TempDirectory? directory = new();
        try
        {
            string anchor = Path.Combine(directory.Path, "anchor");
            string globalHome = Path.Combine(directory.Path, "global-home");
            FakeSquadUserPaths userPaths = new(Path.Combine(directory.Path, "user-paths"));
            Directory.CreateDirectory(anchor);
            Directory.CreateDirectory(globalHome);
            Directory.CreateDirectory(userPaths.ApplicationDataDirectory);

            SquadStateStore stateStore = RealSquadState.Store(userPaths);
            SquadGlobalRoots globalRoots = new(_ => null, globalHome);
            using CorpusSquadReleaseSource releaseSource = new();
            SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(
                releaseSource,
                SquadHostDefaults.ResolveRenderer(),
                stateStore,
                globalRoots: globalRoots);

            SquadLifecycleResult result = service.InstallAsync(new SquadInstallRequest(
                TargetRoot: anchor,
                Scope: SquadDeploymentScope.Global,
                Targets: [SquadTarget.Cursor],
                Version: "1.2.3")).GetAwaiter().GetResult();

            PreparedGlobalInstall prepared = new(
                key,
                directory,
                globalHome,
                userPaths.ApplicationDataDirectory,
                userPaths.HomeDirectory,
                SquadInstallRecord.FromLifecycle(result, exitCode: result.Success ? 0 : 1));
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    /// <summary>
    /// One real project install through <see cref="SquadInstallCommand"/>: the collaborators-injected
    /// install, its dry run, or the install that refuses before writing because the toolchain is unreleased.
    /// </summary>
    private static PreparedCommandRun BuildProjectInstall(string key, bool dryRun, bool unreleasedToolchain)
    {
        TempDirectory? directory = new();
        try
        {
            string targetDir = Path.Combine(directory.Path, "target");
            Directory.CreateDirectory(targetDir);
            FakeSquadUserPaths userPaths = new(Path.Combine(directory.Path, "user-home"));
            SquadStateStore stateStore = RealSquadState.Store(userPaths);
            using FakeSquadReleaseSource? releaseSource = unreleasedToolchain ? null : new FakeSquadReleaseSource();
            FakeSquadRenderer? renderer = unreleasedToolchain ? null : new FakeSquadRenderer();

            SquadInstallCommand command = SquadTestCommands.Install(
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
                    DryRun = dryRun,
                    Adopt = false
                }));

            PreparedCommandRun prepared = new(
                key,
                directory,
                execution,
                targetDir,
                HomeProjectStateDirectory(userPaths, targetDir));
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

    /// <summary>
    /// The dry-run oracle is the file count the injected renderer emits for the desired
    /// pair. A one-target plan cannot report that count.
    /// </summary>
    private static int RenderedDeploymentFileCount(params SquadTarget[] targets)
    {
        FakeSquadRenderer renderer = new();
        SquadRenderResult result = renderer.RenderAsync(
            new SquadRenderRequest(
                SourceDirectory: ".",
                Targets: targets,
                Scope: SquadDeploymentScope.Project).Prepared()).GetAwaiter().GetResult();
        return result.Files.Count;
    }

    private static int PlannedDeployedFileCount(string output)
    {
        const string marker = "planned ";
        const string trailer = " deployed files";
        int markerIndex = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, output);
        int countStart = markerIndex + marker.Length;
        int trailerIndex = output.IndexOf(trailer, countStart, StringComparison.Ordinal);
        Assert.True(trailerIndex > countStart, output);
        string digits = output[countStart..trailerIndex];
        Assert.True(
            int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int count),
            digits);
        return count;
    }

    private static string HomeProjectStateDirectory(ISquadUserPaths userPaths, string projectRoot) =>
        Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "projects",
            SquadPhysicalRootIdentity.Resolve(projectRoot, SquadFileSystemPathSemantics.Instance).Key);

    private static void SeedDeployment(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadStateStore stateStore,
        params (string RelativePath, string Content)[] files)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, scope);
        string lockPath = stateStore.ResolveLockPath(targetRoot, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        List<SquadOwnedFile> ownedFiles = new List<SquadOwnedFile>();
        foreach ((string relPath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ownedFiles.Add(new SquadOwnedFile(relPath, sha256, "codex", Adopted: false));
        }

        SquadReceipt receipt = new SquadReceipt(
            Schema: "kyber-squad.receipt/v1",
            Scope: scope,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: ownedFiles);

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

    /// <summary>
    /// Seeds a Global deployment whose receipt is persisted as authentic legacy v1 JSON — the
    /// shape a genuine pre-#91 CLI wrote, with no explicit <c>layout</c> field — rather than
    /// through <see cref="SquadStateStore.SerializeReceipt"/>, which always upgrades a Global
    /// receipt to v2 regardless of the schema string already on the in-memory object. A
    /// directly constructed receipt with prefixed paths but an unset (default)
    /// <see cref="SquadReceipt.Layout"/> would otherwise round-trip through the v2 writer as a
    /// self-contradicting payload — exactly what the v2 read-side cross-check (#91) now refuses.
    /// </summary>
    private static void SeedLegacyGlobalDeployment(
        string targetRoot,
        SquadStateStore stateStore,
        params (string RelativePath, string Content)[] files)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global);
        string lockPath = stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        JsonArray filesJson = [];
        foreach ((string relPath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            filesJson.Add(new JsonObject
            {
                ["relativePath"] = relPath,
                ["sha256"] = sha256,
                ["target"] = "codex",
                ["adopted"] = false
            });
        }

        JsonObject receiptJson = new JsonObject
        {
            ["schema"] = "kyber-squad.receipt/v1",
            ["scope"] = "global",
            ["targetRoot"] = ".",
            ["installedAtUtc"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["degradations"] = new JsonArray(),
            ["files"] = filesJson
        };

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

        File.WriteAllText(receiptPath, receiptJson.ToJsonString() + "\n", Encoding.UTF8);
        File.WriteAllText(lockPath, stateStore.SerializeLock(squadLock), Encoding.UTF8);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    /// <summary>Every prepared install this class reads, built once for the run before the first test body.</summary>
    private sealed record PreparedCases(
        PreparedGlobalInstall GlobalCursorInstall,
        PreparedCommandRun ProjectInstallWithCollaborators,
        PreparedCommandRun ProjectDryRun,
        PreparedCommandRun ProjectInstallUnreleasedToolchain);

    /// <summary>One real global install, built at most once for the run, that a test copies into its own home.</summary>
    private sealed class PreparedGlobalInstall(
        string key,
        TempDirectory directory,
        string globalHome,
        string userPathsDirectory,
        string homeDirectory,
        SquadInstallRecord record) : PreparedSquadInstall(key, directory, record)
    {
        /// <summary>The home the install resolved every target's global root beneath.</summary>
        public string GlobalHome { get; } = globalHome;

        /// <summary>The application data directory the install recorded its state beneath.</summary>
        public string UserPathsDirectory { get; } = userPathsDirectory;

        /// <summary>The home directory the install recorded as the owner of its global state.</summary>
        public string HomeDirectory { get; } = homeDirectory;

        /// <summary>
        /// Copies the deployment and its state into a test's own home and re-roots both recorded roots, so
        /// the copy is a faithful install at its new root for the doctor scan that follows.
        /// </summary>
        /// <param name="home">The test's home the global roots resolve beneath.</param>
        /// <param name="userPaths">The test's fake user paths, which place the state.</param>
        public void CopyInto(string home, FakeSquadUserPaths userPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(home);
            ArgumentNullException.ThrowIfNull(userPaths);

            SquadInstallCopy.Tree(GlobalHome, home);
            SquadInstallCopy.Tree(UserPathsDirectory, userPaths.ApplicationDataDirectory);

            SquadStateStore store = RealSquadState.Store(userPaths);
            string stateDirectory = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad");
            SquadInstallCopy.ReRoot(
                stateDirectory,
                SquadInstallCopy.Canonical(HomeDirectory),
                SquadInstallCopy.Canonical(userPaths.HomeDirectory));
            SquadInstallCopy.ReRoot(
                stateDirectory,
                SquadInstallCopy.Canonical(GlobalHome),
                SquadInstallCopy.Canonical(home));
            _ = SquadInstallCopy.ReRootDeployed(
                home,
                store.ResolveReceiptPath(home, SquadDeploymentScope.Global),
                store,
                SquadInstallCopy.Canonical(GlobalHome),
                SquadInstallCopy.Canonical(home));
        }
    }

    /// <summary>One real command run, built at most once for the run, that a test reads as a record.</summary>
    private sealed class PreparedCommandRun(
        string key,
        TempDirectory directory,
        CommandExecution execution,
        string targetRoot,
        string homeStateDirectory) : PreparedSquadInstall(key, directory, new SquadInstallRecord
        {
            ExitCode = execution.ExitCode,
            StandardOutput = execution.Output
        })
    {
        /// <summary>The project root the command ran against.</summary>
        public string TargetRoot { get; } = targetRoot;

        /// <summary>The home state directory the install wrote, or would write, for that root.</summary>
        public string HomeStateDirectory { get; } = homeStateDirectory;
    }

    private sealed class FakeGlobalRootResolver(SquadTarget target, string root)
        : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget requested) =>
            requested == target
                ? root
                : throw new ArgumentOutOfRangeException(nameof(requested), requested, "No root mapped for target.");
    }

    private sealed class SquadRepoFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public string Path => _temp.Path;

        public static SquadRepoFixture CreateValid()
        {
            SquadRepoFixture fixture = new SquadRepoFixture();
            fixture.Write("KyberWeave.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");
            fixture.Write("products/kyber-squad/squad.yml", """
                schema: kyber-squad.squad/v1
                name: kyber-squad
                version-source: kyber-weave-assembly
                default-bundle: full
                bundles:
                  full: bundles/full.yml
                profiles:
                  models: profiles/models.yml
                  capabilities: profiles/capabilities.yml
                  fallbacks: profiles/fallbacks.yml
                toolchain: toolchain.yml
                mcp: mcp.json
                """);
            fixture.Write("products/kyber-squad/bundles/full.yml", """
                schema: kyber-squad.bundle/v1
                name: full
                agents:
                  - architect
                  - csharp-dev
                skills:
                  - test-dev
                """);
            fixture.Write("products/kyber-squad/profiles/models.yml", """
                schema: kyber-squad.model-profiles/v1
                profiles:
                  deep-planning:
                    default: inherit
                  general:
                    default: inherit
                """);
            fixture.Write("products/kyber-squad/profiles/capabilities.yml", """
                schema: kyber-squad.capability-profiles/v1
                capabilities:
                  - filesystem.read
                  - filesystem.write
                  - delegate
                profiles:
                  architect:
                    permissions:
                      filesystem.read: allow
                      filesystem.write: deny
                      delegate: ask
                  worker:
                    permissions:
                      filesystem.read: allow
                      filesystem.write: ask
                      delegate: deny
                """);
            fixture.Write("products/kyber-squad/profiles/fallbacks.yml", """
                schema: kyber-squad.fallback-profiles/v1
                profiles:
                  role-skill:
                    no-primary-agent: skill
                    no-agent-primitive: skill
                """);
            fixture.Write("products/kyber-squad/toolchain.yml", """
                schema: kyber-squad.toolchain/v1
                required-features:
                  - agent-ir/v1
                validated-release: null
                """);
            fixture.Write("products/kyber-squad/mcp.json", """
                {
                  "mcpServers": {
                    "kyber-weave": {
                      "command": "kyber-weave-mcp",
                      "args": ["--repo-root", "."]
                    }
                  }
                }
                """);
            fixture.Write("products/kyber-squad/agents/architect.md", """
                ---
                schema: kyber-squad.agent/v1
                name: architect
                description: Use when planning multi-domain work.
                invocation: subagent
                model-profile: deep-planning
                capability-profile: architect
                copilot-tools: [vscode]
                delegates-to: [csharp-dev]
                fallback: role-skill
                aliases: []
                ---
                You are architect.
                Plan first.
                """);
            fixture.Write("products/kyber-squad/agents/csharp-dev.md", """
                ---
                schema: kyber-squad.agent/v1
                name: csharp-dev
                description: Use for .NET work.
                invocation: subagent
                model-profile: general
                capability-profile: worker
                copilot-tools: [vscode]
                delegates-to: []
                fallback: role-skill
                aliases: []
                ---
                You are csharp-dev.
                """);
            fixture.Write("products/kyber-squad/skills/test-dev/SKILL.md", """
                ---
                name: test-dev
                description: Use when writing tests.
                ---
                Test first.
                """);

            foreach (string schema in new[] { "squad", "bundle", "agent", "model-profiles", "capability-profiles" })
            {
                fixture.Write($"products/kyber-squad/schemas/{schema}.schema.json", """
                    {
                      "$schema": "https://json-schema.org/draft/2020-12/schema",
                      "type": "object"
                    }
                    """);
            }

            fixture.Write("products/kyber-squad/schemas/fallback-profiles.schema.json", """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "$id": "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
                  "type": "object"
                }
                """);

            return fixture;
        }

        private void Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }

        public void Dispose() => _temp.Dispose();
    }

    #endregion
}

