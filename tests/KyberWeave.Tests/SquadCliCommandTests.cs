using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task K6 test suite: Pins the Kyber-Squad CLI command surface, settings, execution semantics,
/// and source discovery boundaries before toolchain qualification and runtime rendering.
/// Covers Test Contract K6 (K6a, K6b, K6c, K6d) from docs/plans/2026-08-14-kyber-squad-unified-agent-skill-deployment.md.
/// </summary>
public sealed class SquadCliCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

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

        // Assert CLI and Core assemblies have no embedded resource for products/kyber-squad corpus
        Assembly cliAssembly = typeof(SquadPackSourceLocator).Assembly;
        Assembly coreAssembly = typeof(SquadSource).Assembly;

        string[] cliResources = cliAssembly.GetManifestResourceNames();
        string[] coreResources = coreAssembly.GetManifestResourceNames();

        Assert.DoesNotContain(cliResources, name => name.Contains("products", StringComparison.OrdinalIgnoreCase) ||
                                                    name.Contains("kyber-squad", StringComparison.OrdinalIgnoreCase) ||
                                                    name.EndsWith("squad.yml", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(coreResources, name => name.Contains("products", StringComparison.OrdinalIgnoreCase) ||
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
        SquadPackCommand command = new SquadPackCommand(executor, workingDirectory: workingDir);

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
        SquadPackCommand command = new SquadPackCommand(executor, workingDirectory: _temp.Path);

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
        SquadPackCommand command = new SquadPackCommand(executor, workingDirectory: repoPath);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStatusCommand command = new SquadStatusCommand(userPaths);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        SquadStatusCommand command = new SquadStatusCommand(userPaths);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        // Modify one file on disk to simulate drift
        string driftedFilePath = Path.Combine(targetDir, "agents", "architect.md");
        File.WriteAllText(driftedFilePath, "Locally modified content.");

        SquadStatusCommand command = new SquadStatusCommand(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("agents/architect.md", execution.Output, StringComparison.Ordinal);
        Assert.Contains("drift", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_MissingFile_ExitsOneAndIdentifiesMissingFile()
    {
        string targetDir = Path.Combine(_temp.Path, "missing-file-project");
        Directory.CreateDirectory(targetDir);

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        // Delete one file
        File.Delete(Path.Combine(targetDir, "skills", "test-dev", "SKILL.md"));

        SquadStatusCommand command = new SquadStatusCommand(userPaths);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

        SeedDeployment(globalRoot, SquadDeploymentScope.Global, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        SquadStatusCommand command = new SquadStatusCommand(userPaths);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new SquadDoctorCommand(executor, userPaths, workingDirectory: installedDir);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new SquadDoctorCommand(executor, userPaths, workingDirectory: workingDir);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = workingDir,
                Global = false
            }));

        // Copilot (native) and Antigravity (fallback) are registered; other approved
        // targets still report as not-yet-implemented rather than silently absent.
        Assert.Contains("copilot", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("antigravity", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs/todo", execution.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-mcp", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.2.3", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WhenMcpProbeFails_ReportsFailureReason()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-missing-mcp");
        Directory.CreateDirectory(workingDir);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithFailure("kyber-weave-mcp", "The 'kyber-weave-mcp' executable is not available on PATH.");

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new SquadDoctorCommand(executor, userPaths, workingDirectory: workingDir);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new SquadDoctorCommand(executor, userPaths, workingDirectory: repoPath);

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

    #endregion

    #region SquadUninstallCommand Execution Tests

    [Fact]
    public void Uninstall_MissingReceipt_ExitsZeroAsNoOp()
    {
        string targetDir = Path.Combine(_temp.Path, "no-receipt-uninstall");
        Directory.CreateDirectory(targetDir);

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadUninstallCommand command = new SquadUninstallCommand(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                Path = targetDir,
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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        string deployedFile1 = Path.Combine(targetDir, "agents", "architect.md");
        string deployedFile2 = Path.Combine(targetDir, "skills", "test-dev", "SKILL.md");
        string receiptPath = Path.Combine(targetDir, ".kyber-weave", "squad.receipt.json");
        string lockPath = Path.Combine(targetDir, ".kyber-weave", "squad.lock.yml");

        Assert.True(File.Exists(deployedFile1));
        Assert.True(File.Exists(deployedFile2));
        Assert.True(File.Exists(receiptPath));
        Assert.True(File.Exists(lockPath));

        SquadUninstallCommand command = new SquadUninstallCommand(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                Path = targetDir,
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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        string deployedFile1 = Path.Combine(targetDir, "agents", "architect.md");
        string receiptPath = Path.Combine(targetDir, ".kyber-weave", "squad.receipt.json");

        SquadUninstallCommand command = new SquadUninstallCommand(userPaths);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                Path = targetDir,
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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

        string receiptPath = stateStore.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, "invalid json {[[");

        SquadUninstallCommand command = new SquadUninstallCommand(userPaths, stateStore);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                Path = targetDir,
                Global = false,
                DryRun = false
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region SquadInstallCommand and SquadUpdateCommand Execution Tests

    [Fact]
    public void Install_WhenToolchainUnreleased_FailsClosedWithExitOne()
    {
        string targetDir = Path.Combine(_temp.Path, "install-target");
        Directory.CreateDirectory(targetDir);

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadInstallCommand command = new SquadInstallCommand(userPaths);

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

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("error", execution.Output, StringComparison.OrdinalIgnoreCase);
        // Ensure no target files or state were written
        Assert.False(Directory.Exists(Path.Combine(targetDir, ".kyber-weave")));
    }

    [Fact]
    public void Install_WhenCollaboratorsInjected_SucceedsWithExitZero()
    {
        string targetDir = Path.Combine(_temp.Path, "install-success-target");
        Directory.CreateDirectory(targetDir);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
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
        Assert.Contains("Successfully installed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(targetDir, ".kyber-weave")));
        Assert.True(File.Exists(Path.Combine(targetDir, ".kyber-weave", "squad.lock.yml")));
        Assert.True(File.Exists(Path.Combine(targetDir, ".kyber-weave", "squad.receipt.json")));
    }

    [Fact]
    public void Install_DryRun_ReportsPlannedFilesWithoutModifyingFilesystem()
    {
        string targetDir = Path.Combine(_temp.Path, "install-dryrun-target");
        Directory.CreateDirectory(targetDir);

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
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
                DryRun = true,
                Adopt = false
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Dry-run", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(targetDir, ".kyber-weave")));
    }

    [Theory]
    [InlineData("invalid-target")]
    [InlineData("codex,unknown-target")]
    public void Install_InvalidTargetToken_ExitsTwo(string target)
    {
        string targetDir = Path.Combine(_temp.Path, "install-invalid-target");
        Directory.CreateDirectory(targetDir);

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadInstallCommand command = new SquadInstallCommand(userPaths);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadInstallCommand command = new SquadInstallCommand(userPaths);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadInstallCommand command = new SquadInstallCommand(userPaths);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        SquadUpdateCommand command = new SquadUpdateCommand(userPaths);

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
        FakeUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
            (".codex/agents/architect.toml", "name = \"architect\"\n"));

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
        Assert.Contains("Successfully updated", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invalid-target")]
    [InlineData("claude,unknown-target")]
    public void Update_InvalidTargetToken_ExitsTwo(string target)
    {
        string targetDir = Path.Combine(_temp.Path, "update-invalid-target");
        Directory.CreateDirectory(targetDir);

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadUpdateCommand command = new SquadUpdateCommand(userPaths);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);

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

        SquadUpdateCommand command = new SquadUpdateCommand(userPaths, stateStore);

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

        FakeUserPaths userPaths = new FakeUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadUpdateCommand command = new SquadUpdateCommand(userPaths);

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
        SquadInstallCommand install = new SquadInstallCommand();
        SquadUpdateCommand update = new SquadUpdateCommand();
        SquadUninstallCommand uninstall = new SquadUninstallCommand();
        SquadStatusCommand status = new SquadStatusCommand();
        SquadDoctorCommand doctor = new SquadDoctorCommand();
        SquadPackCommand pack = new SquadPackCommand();

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
        SquadStateStore defaultStore = SquadCommandComposition.ResolveStateStore();
        Assert.NotNull(defaultStore);

        string existingDir = _temp.Path;
        string appDataDir = Path.Combine(_temp.Path, "custom-appdata");
        FakeUserPaths fakePaths = new FakeUserPaths(appDataDir);
        SquadStateStore customStore = SquadCommandComposition.ResolveStateStore(fakePaths);
        Assert.NotNull(customStore);
        Assert.StartsWith(
            appDataDir,
            customStore.ResolveReceiptPath(existingDir, SquadDeploymentScope.Global),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SquadCommandComposition_ResolveProbe_ResolvesWithDefaultAndCustomExecutor()
    {
        McpProcessProbe defaultMcp = SquadCommandComposition.ResolveProbe();
        Assert.NotNull(defaultMcp);

        FakeProcessExecutor executor = new FakeProcessExecutor();
        McpProcessProbe customMcp = SquadCommandComposition.ResolveProbe(executor);
        Assert.NotNull(customMcp);
    }

    [Fact]
    public void SquadCommandComposition_ResolveTransaction_ReturnsTransactionWithResolvedState()
    {
        SquadTransaction txDefault = SquadCommandComposition.ResolveTransaction();
        Assert.NotNull(txDefault);

        FakeUserPaths fakePaths = new FakeUserPaths("/custom/appdata");
        SquadStateStore store = new SquadStateStore(fakePaths);
        SquadTransaction txWithStore = SquadCommandComposition.ResolveTransaction(store);
        Assert.NotNull(txWithStore);

        SquadTransaction txWithPaths = SquadCommandComposition.ResolveTransaction(null, fakePaths);
        Assert.NotNull(txWithPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SquadCommandComposition_ResolveTargetRoot_NullOrWhitespace_ReturnsCurrentDirectoryFullPath(string? input)
    {
        string resolved = SquadCommandComposition.ResolveTargetRoot(input);
        Assert.Equal(Path.GetFullPath("."), resolved);
    }

    [Fact]
    public void SquadCommandComposition_ResolveTargetRoot_SpecifiedPath_ReturnsFullPath()
    {
        string relative = "some/relative/dir";
        string resolved = SquadCommandComposition.ResolveTargetRoot(relative);
        Assert.Equal(Path.GetFullPath(relative), resolved);
    }

    [Theory]
    [InlineData(true, SquadDeploymentScope.Global)]
    [InlineData(false, SquadDeploymentScope.Project)]
    public void SquadCommandComposition_ResolveScope_MapsCorrectly(bool isGlobal, SquadDeploymentScope expected)
    {
        Assert.Equal(expected, SquadCommandComposition.ResolveScope(isGlobal));
    }

    [Fact]
    public void SquadCommandComposition_IsInteractiveConsole_ReturnsBooleanWithoutThrowing()
    {
        bool isInteractive = SquadCommandComposition.IsInteractiveConsole();
        Assert.True(isInteractive || !isInteractive);
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
            "Program.cs"));

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
    }

    #endregion

    #region Test Fixtures and Helpers

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

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

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class FakeUserPaths(string appDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = appDataDirectory;
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
                      "args": []
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
