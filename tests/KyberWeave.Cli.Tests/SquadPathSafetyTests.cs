using KyberWeave.Testing.Squad;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;
using KyberWeave.Data.Squad.Packaging;

namespace KyberWeave.Tests;

/// <summary>
/// Contract suite for squad target-root argument safety (development mode: test-first,
/// docs/plans/2026-09-23-squad-path-argument-safety.md, §6 row T3). Pins the seams tasks
/// T4/T5 must implement: the long-only <c>--path</c> option binding onto the separate
/// <c>PathOption</c> property under a strict-configured CommandApp, the
/// positional/option coalescing and conflict rule, the injectable target-root
/// confirmation matrix, and the decline/non-interactive wiring in each mutating
/// command (install, update, and uninstall).
/// Authored before the seams exist: every compile failure against
/// <c>PathOption</c>, <c>CoalesceTargetPath</c>, <c>SquadTargetRootConfirmation</c>, and
/// the optional <c>isInteractive</c>/<c>readAnswer</c> constructor parameters is the
/// recorded RED evidence, as is the failing <c>UseStrictParsing</c> assertion in
/// SquadCliCommandTests.
///
/// A second RED phase (post-closeout review remediation, PR #113) pins two review
/// findings the T3 seams did not cover. A <c>--global</c> confirmation must echo each
/// selected target's physical global root — the directories the lifecycle actually
/// writes — not only the global state anchor, whose display left the operator approving
/// a mutation to directories nothing named. And <c>squad doctor</c> must consume the
/// <c>--path</c> option and the positional it accepts, diagnosing the named root with
/// the same positional/option conflict rule as the other commands, instead of always
/// diagnosing the working directory. Until the implementation lands, the
/// per-target-root Contains assertions and the doctor exit-code assertions are the
/// recorded RED evidence.
/// </summary>
/// <remarks>
/// The confirmation helper's <c>readAnswer</c> seam is contracted as
/// <c>Func&lt;string, bool&gt;</c> (question in, answer out) so tests inject the decision
/// without coupling to yes/no parsing conventions. Everything runs offline: release
/// fetches go through <see cref="FakeSquadReleaseSource"/>, and interactivity is injected
/// through delegates because xunit-redirected console streams make every real-console
/// check report non-interactive. The <c>--global</c> pins sandbox the real per-target
/// root resolution through the same overrides production reads first
/// (<c>CODEX_HOME</c>, <c>CLAUDE_CONFIG_DIR</c>), so the sandbox roots are where the runs
/// write; here the in-memory <c>SquadGlobalRoots</c> is handed those overrides directly instead of the process environment.
/// </remarks>
public sealed class SquadPathSafetyTests
{
    private readonly InMemorySquadState _state = new();
    private readonly FakeSquadRenderer _renderer = new();

    private string Work => Path.Combine(_state.Files.Root, "work");

    #region Parser contract (§6 T3 row a): strict parsing and --path binding

    [Fact]
    public void StrictCommandApp_BindsPathOptionOntoSquadInstallSettings()
    {
        // --path must bind the separate PathOption property, leaving the positional
        // argument at its "." default; the coalescing rule (N6) then lets --path win.
        string deploymentRoot = Path.Combine(Work, "bind-root");

        PathBindingStubCommand.Captured = null;
        (int ExitCode, string Output) execution = RunThroughStrictApp(["install", "--path", deploymentRoot]);

        Assert.Equal(0, execution.ExitCode);
        SquadInstallSettings? captured = PathBindingStubCommand.Captured;
        Assert.NotNull(captured);
        Assert.Equal(deploymentRoot, captured.PathOption);
        Assert.Equal(".", captured.Path);
    }

    [Fact]
    public void StrictCommandApp_UnknownOption_ExitsNonZeroAndNamesTheOffender()
    {
        // The incident: relaxed parsing silently discarded an unrecognized option with
        // its value and proceeded against the current directory. Strict parsing must
        // hard-error with the offender named instead.
        PathBindingStubCommand.Captured = null;
        (int ExitCode, string Output) execution = RunThroughStrictApp(["install", "--definitely-not-an-option", "value"]);

        Assert.NotEqual(0, execution.ExitCode);
        Assert.Contains("definitely-not-an-option", Normalize(execution.Output), StringComparison.OrdinalIgnoreCase);
        Assert.Null(PathBindingStubCommand.Captured);
    }

    [Fact]
    public void StrictCommandApp_SurplusPositional_ExitsNonZero()
    {
        // Strict mode also rejects surplus positionals (plan §10); the tighter behavior
        // is intended and pinned so it cannot quietly regress to relaxed parsing.
        PathBindingStubCommand.Captured = null;
        (int ExitCode, string Output) execution = RunThroughStrictApp(["install", "first", "second"]);

        Assert.NotEqual(0, execution.ExitCode);
    }

    #endregion

    #region Coalescing contract (§6 T3 row b): positional vs --path

    [Theory]
    [InlineData(".", "/absolute/option-root")]
    [InlineData(".", "relative/option-root")]
    [InlineData("", "option-root")]
    [InlineData("   ", "option-root")]
    public void CoalesceTargetPath_WhenOptionSupplied_OptionWinsOverTheDefaultPositional(string positional, string option)
    {
        // N6: --path with the positional at its "." default is the normal case and
        // --path wins. The option value passes through verbatim; ResolveTargetRoot
        // absolutizes it afterwards.
        Assert.Equal(option, SquadCommandComposition.CoalesceTargetPath(positional, option));
    }

    [Theory]
    [InlineData("custom-positional-root")]
    [InlineData("./sub/dir")]
    public void CoalesceTargetPath_WhenNoOptionSupplied_PositionalPassesThrough(string positional)
    {
        // The positional argument remains the documented form; coalescing must not
        // disturb the option-free path every existing caller relies on.
        Assert.Equal(positional, SquadCommandComposition.CoalesceTargetPath(positional, null));
    }

    [Fact]
    public void CoalesceTargetPath_WhenPositionalAndOptionConflict_ThrowsNamingBothForms()
    {
        // N6: supplying both a non-default positional and --path is a conflict. The
        // failure follows the token-validation catch pattern (ArgumentException, which
        // the commands translate into exit 2) and the hint names both forms so the
        // operator can pick one.
        string positional = "conflicting-positional-root";

        ArgumentException conflict = Assert.Throws<ArgumentException>(
            () => SquadCommandComposition.CoalesceTargetPath(positional, "conflicting-option-root"));

        Assert.Contains(positional, conflict.Message, StringComparison.Ordinal);
        Assert.Contains("--path", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_WhenPositionalAndPathOptionConflict_ExitsTwoNamingBothForms()
    {
        // The command wiring surfaces the conflict as the convention's exit-2 abort
        // before any resolution or filesystem work, with the hint naming both forms.
        string targetDir = Path.Combine(Work, "conflict-target");
        _state.Files.CreateDirectory(targetDir);

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(_state, releaseSource, _renderer);

        (int ExitCode, string Output) execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadInstallSettings
            {
                Path = Path.Combine(Work, "conflicting-positional-root"),
                PathOption = Path.Combine(Work, "conflicting-option-root"),
                Targets = ["codex"]
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        string normalized = Normalize(execution.Output);
        Assert.Contains("conflicting-positional-root", normalized, StringComparison.Ordinal);
        Assert.Contains("--path", normalized, StringComparison.Ordinal);
        Assert.False(_state.Files.DirectoryExists(Path.Combine(targetDir, ".kyber-weave")));
    }

    #endregion

    #region Command wiring contract (§6 T3 row d): confirmation before mutation

    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("uninstall")]
    public async Task MutatingVerb_WhenInteractiveConfirmationDeclined_ExitsTwoWithoutWritingAnything(string verb)
    {
        // N3 wiring, pinned per mutating command because the gate is wired separately in
        // each: a decline is aborted-before-side-effects client input — exit 2 with the
        // decline message, and the lifecycle call (the only side-effecting step) must
        // never have been reached: zero release requests, zero filesystem writes. Update
        // sits behind a receipt prerequisite, so its zero-writes proof is the seeded tree
        // being left byte-for-byte unchanged rather than the directory being absent.
        string targetDir = Path.Combine(Work, $"decline-{verb}-target");
        _state.Files.CreateDirectory(targetDir);

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadStateStore stateStore = _state.Store;
        if (verb == "update")
        {
            SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
                (".codex/agents/architect.toml", "name = \"architect\"\n"));
        }

        string[] filesBefore = Files(targetDir);

        (int ExitCode, string Output) execution = await CaptureAsync(() => verb switch
        {
            "install" => SquadTestCommands.InMemoryInstall(
                _state,
                releaseSource,
                renderer,
                isInteractive: true,
                readAnswer: _ => false).ExecuteAsync(
                null!,
                new SquadInstallSettings
                {
                    Path = targetDir,
                    Targets = ["codex"],
                    Global = false,
                    DryRun = false,
                    Adopt = false
                },
                CancellationToken.None),
            "update" => SquadTestCommands.InMemoryUpdate(
                _state,
                releaseSource,
                renderer,
                isInteractive: true,
                readAnswer: _ => false).ExecuteAsync(
                null!,
                new SquadUpdateSettings
                {
                    Path = targetDir,
                    Targets = ["codex"],
                    Global = false,
                    DryRun = false
                },
                CancellationToken.None),
            _ => SquadTestCommands.InMemoryUninstall(
                _state,
                releaseSource,
                renderer,
                isInteractive: true,
                readAnswer: _ => false).ExecuteAsync(
                null!,
                new SquadUninstallSettings
                {
                    PathOption = targetDir,
                    Global = false,
                    DryRun = false
                },
                CancellationToken.None)
        });

        Assert.Equal(2, execution.ExitCode);
        string normalized = Normalize(execution.Output);
        Assert.Contains("Declined. No changes were made.", normalized, StringComparison.Ordinal);
        Assert.Contains($"squad {verb}", normalized, StringComparison.Ordinal);
        string[] filesAfter = Files(targetDir);
        if (verb == "update")
        {
            Assert.Equal(filesBefore, filesAfter);
        }
        else
        {
            Assert.False(_state.Files.DirectoryExists(Path.Combine(targetDir, ".kyber-weave")));
        }

        Assert.Empty(releaseSource.Requests);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("uninstall")]
    public async Task MutatingVerb_WhenNonInteractive_ProceedsWithoutPromptingAndEchoesTheAbsoluteRoot(string verb)
    {
        // N1 wiring, pinned per mutating command because the gate is wired separately in
        // each: a non-interactive mutating run echoes the resolved absolute root and
        // scope, then proceeds to a successful mutation without any prompt. The scope
        // word is asserted because the pre-existing success line does not name it — its
        // presence is what proves the N4 echo happened. Update and uninstall cannot
        // reach their gates without an existing deployment, so those rows seed one.
        string targetDir = Path.Combine(Work, $"echo-root-{verb}-target");
        _state.Files.CreateDirectory(targetDir);

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        SquadStateStore stateStore = _state.Store;
        if (verb is "update" or "uninstall")
        {
            SeedDeployment(targetDir, SquadDeploymentScope.Project, stateStore,
                verb == "update"
                    ? (".codex/agents/architect.toml", "name = \"architect\"\n")
                    : ("agents/architect.md", "You are architect.\nPlan first.\n"));
        }

        int promptCount = 0;

        (int ExitCode, string Output) execution = await CaptureAsync(() => verb switch
        {
            "install" => SquadTestCommands.InMemoryInstall(
                _state,
                releaseSource,
                renderer,
                isInteractive: false,
                readAnswer: _ =>
                {
                    promptCount++;
                    return true;
                }).ExecuteAsync(
                null!,
                new SquadInstallSettings
                {
                    Path = targetDir,
                    Targets = ["codex"],
                    Global = false,
                    DryRun = false,
                    Adopt = false
                },
                CancellationToken.None),
            "update" => SquadTestCommands.InMemoryUpdate(
                _state,
                releaseSource,
                renderer,
                isInteractive: false,
                readAnswer: _ =>
                {
                    promptCount++;
                    return true;
                }).ExecuteAsync(
                null!,
                new SquadUpdateSettings
                {
                    Path = targetDir,
                    Targets = ["codex"],
                    Global = false,
                    DryRun = false,
                    ReplaceManaged = true
                },
                CancellationToken.None),
            _ => SquadTestCommands.InMemoryUninstall(
                _state,
                releaseSource,
                renderer,
                isInteractive: false,
                readAnswer: _ =>
                {
                    promptCount++;
                    return true;
                }).ExecuteAsync(
                null!,
                new SquadUninstallSettings
                {
                    PathOption = targetDir,
                    Global = false,
                    DryRun = false
                },
                CancellationToken.None)
        });

        string successfulMutation = verb switch
        {
            "install" => "Successfully installed",
            "update" => "Successfully updated",
            _ => "Successfully uninstalled"
        };

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(0, promptCount);
        string normalized = Normalize(execution.Output);
        Assert.Contains(Path.GetFullPath(targetDir), normalized, StringComparison.Ordinal);
        Assert.Contains("project", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"squad {verb}", normalized, StringComparison.Ordinal);
        Assert.Contains(successfulMutation, normalized, StringComparison.Ordinal);
        if (verb == "install")
        {
            // Project state lives in the home store, so the project carries no in-repo squad state. The
            // transaction journal may still sit beside the root, which is why only the squad state
            // directory is asserted absent.
            Assert.NotNull(stateStore.ReadLock(targetDir, SquadDeploymentScope.Project));
            Assert.False(_state.Files.DirectoryExists(Path.Combine(targetDir, ".kyber-weave", "squad")));
        }
    }

    #endregion

    #region Global echo truthfulness (review remediation): the confirmation names the per-target physical roots

    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("uninstall")]
    public async Task MutatingVerb_WhenGlobalScope_EchoNamesEachSelectedTargetPhysicalGlobalRoot(string verb)
    {
        // Review finding (Greptile P1): with --global the confirmation echoed only
        // targetRoot — the global state anchor — while the lifecycle writes every deployed
        // file beneath the selected target's own physical global root: at plan time
        // SquadDeploymentPlan resolves each mutation through
        // ISquadGlobalRootResolver.ResolveGlobalRoot(target). Install, update, and
        // uninstall asked the operator to approve a mutation while showing directories
        // other than those that would actually change. The contract pinned here: for a
        // --global run the echoed confirmation names every selected target's physical
        // global root.
        //
        // The test drives the real commands with the inputs production resolution uses:
        // the CLI's global-root resolver reads CODEX_HOME/CLAUDE_CONFIG_DIR ahead of the
        // home-relative defaults, so the overrides make the sandbox roots the honest
        // mutation targets, and the runs really do succeed into them. The state anchor and
        // the success lines may still name targetRoot after the fix — the contract is that
        // the per-target roots appear, not that the anchor is suppressed.
        string anchorRoot = Path.Combine(Work, $"global-echo-{verb}-anchor");
        _state.Files.CreateDirectory(anchorRoot);
        string codexGlobalRoot = Path.Combine(Work, $"global-echo-{verb}-codex-home");
        _state.Files.CreateDirectory(codexGlobalRoot);
        string claudeGlobalRoot = Path.Combine(Work, $"global-echo-{verb}-claude-home");
        _state.Files.CreateDirectory(claudeGlobalRoot);

        SquadStateStore stateStore = _state.Store;
        if (verb is "update" or "uninstall")
        {
            // Update and uninstall cannot reach their gates without an existing global
            // deployment, and uninstall has no --target option — its targets are the
            // receipt's — so the seeded receipt names a file per target.
            SeedGlobalDeployment(anchorRoot, stateStore,
                ("agents/architect.md", "You are architect.\nPlan first.\n", "codex"),
                ("agents/reviewer.md", "You are reviewer.\nReview changes.\n", "claude"));
        }

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = _renderer;
        int promptCount = 0;

        Dictionary<string, string> globalRootOverrides = new(StringComparer.Ordinal)
        {
            ["CODEX_HOME"] = codexGlobalRoot,
            ["CLAUDE_CONFIG_DIR"] = claudeGlobalRoot
        };
        SquadGlobalRoots globalRoots = new(
            name => globalRootOverrides.GetValueOrDefault(name),
            _state.UserPaths.HomeDirectory);

        (int ExitCode, string Output) execution = await CaptureAsync(() => verb switch
        {
            "install" => SquadTestCommands.InMemoryInstall(
                _state,
                releaseSource,
                renderer,
                globalRoots: globalRoots,
                isInteractive: true,
                readAnswer: _ =>
                {
                    promptCount++;
                    return true;
                }).ExecuteAsync(
                null!,
                new SquadInstallSettings
                {
                    Path = anchorRoot,
                    Targets = ["codex", "claude"],
                    Global = true,
                    DryRun = false,
                    Adopt = false
                },
                CancellationToken.None),
            "update" => SquadTestCommands.InMemoryUpdate(
                _state,
                releaseSource,
                renderer,
                globalRoots: globalRoots,
                isInteractive: true,
                readAnswer: _ =>
                {
                    promptCount++;
                    return true;
                }).ExecuteAsync(
                null!,
                new SquadUpdateSettings
                {
                    Path = anchorRoot,
                    Targets = ["codex", "claude"],
                    Global = true,
                    DryRun = false,
                    ReplaceManaged = true
                },
                CancellationToken.None),
            _ => SquadTestCommands.InMemoryUninstall(
                _state,
                releaseSource,
                renderer,
                globalRoots: globalRoots,
                isInteractive: true,
                readAnswer: _ =>
                {
                    promptCount++;
                    return true;
                }).ExecuteAsync(
                null!,
                new SquadUninstallSettings
                {
                    PathOption = anchorRoot,
                    Global = true,
                    DryRun = false
                },
                CancellationToken.None)
        });

        string normalized = Normalize(execution.Output);

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(1, promptCount);
        Assert.Contains(codexGlobalRoot, normalized, StringComparison.Ordinal);
        Assert.Contains(claudeGlobalRoot, normalized, StringComparison.Ordinal);
    }

    #endregion

    #region Doctor path consumption (review remediation): --path and the positional select the diagnosed root

    [Fact]
    public async Task Doctor_WhenPathOptionSupplied_DiagnosesTheNamedRootNotTheDefaultRoot()
    {
        // Review finding (Greptile P1): squad doctor accepted --path and then ignored it —
        // Execute diagnosed the injected default (the working directory, the process's
        // current directory in production), so a run could report health for one directory
        // while the operator pointed at another. The canonical source check is the
        // root-observable section: --path aimed at a root whose squad.yml does not parse
        // must fail doctor for THAT root. RED today: the option never reaches Execute, the
        // source-less default root is diagnosed instead, and doctor exits 0.
        string defaultRoot = Path.Combine(Work, "doctor-option-default-root");
        _state.Files.CreateDirectory(defaultRoot);
        string requestedRoot = Path.Combine(Work, "doctor-option-requested-root");
        WriteUnparsableCanonicalSource(requestedRoot);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: defaultRoot);

        (int ExitCode, string Output) execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadDoctorSettings
            {
                PathOption = requestedRoot
            },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains(
            "Canonical source validation failed",
            Normalize(execution.Output),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_WhenPositionalPathSupplied_DiagnosesTheNamedRootNotTheDefaultRoot()
    {
        // The positional form carries the same contract: doctor reads
        // CoalesceTargetPath(settings.Path, settings.PathOption) like every other squad
        // command, so `squad doctor <root>` diagnoses that root. RED today: the positional
        // is equally unread, and the source-less default root is diagnosed instead.
        string defaultRoot = Path.Combine(Work, "doctor-positional-default-root");
        _state.Files.CreateDirectory(defaultRoot);
        string requestedRoot = Path.Combine(Work, "doctor-positional-requested-root");
        WriteUnparsableCanonicalSource(requestedRoot);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: defaultRoot);

        (int ExitCode, string Output) execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadDoctorSettings
            {
                Path = requestedRoot
            },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains(
            "Canonical source validation failed",
            Normalize(execution.Output),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_WhenNamedRootHasNoCanonicalSource_DoesNotReportTheDefaultRootsSource()
    {
        // The inverse direction of the same contract, and the exact shape of the reported
        // defect: the default root here carries an unparsable canonical source — diagnosing
        // it reports a validation failure and exits 1 — while the operator named an empty
        // root with no source at all. Diagnosing the named root finds no source and exits
        // 0; diagnosing the default root reports the default's source. RED today: doctor
        // reports the default root's broken source for a run that named the empty root.
        string defaultRoot = Path.Combine(Work, "doctor-inverse-default-root");
        WriteUnparsableCanonicalSource(defaultRoot);
        string requestedRoot = Path.Combine(Work, "doctor-inverse-requested-root");
        _state.Files.CreateDirectory(requestedRoot);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: defaultRoot);

        (int ExitCode, string Output) execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadDoctorSettings
            {
                PathOption = requestedRoot
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("Canonical source", Normalize(execution.Output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_WhenPositionalAndPathOptionConflict_ExitsTwoNamingBothForms()
    {
        // The same N6 rule the other squad commands pin: a non-default positional and
        // --path are two names for one deployment root, and doctor must reject the
        // ambiguity with the exit-2 client-input convention and a hint naming both forms —
        // not silently prefer one. RED today: doctor reads neither form, so the conflict
        // goes undetected and the command exits 0.
        string positionalRoot = Path.Combine(Work, "doctor-conflict-positional-root");
        string optionRoot = Path.Combine(Work, "doctor-conflict-option-root");
        string defaultRoot = Path.Combine(Work, "doctor-conflict-default-root");
        _state.Files.CreateDirectory(defaultRoot);

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: defaultRoot);

        (int ExitCode, string Output) execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadDoctorSettings
            {
                Path = positionalRoot,
                PathOption = optionRoot
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        string normalized = Normalize(execution.Output);
        Assert.Contains(positionalRoot, normalized, StringComparison.Ordinal);
        Assert.Contains("--path", normalized, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes a repository-marker layout whose canonical squad manifest does not parse:
    /// both markers <c>SquadPackSourceLocator</c> requires are present, so the source IS
    /// found at this root and doctor attempts to load it — and the load then fails
    /// validation, which is the only doctor output that names the diagnosed root.
    /// </summary>
    private void WriteUnparsableCanonicalSource(string root)
    {
        _state.Files.CreateDirectory(Path.Combine(root, "products", "kyber-squad"));
        _state.Files.WriteAllText(Path.Combine(root, "KyberWeave.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00");
        _state.Files.WriteAllText(Path.Combine(root, "products", "kyber-squad", "squad.yml"), "this is not a valid squad manifest\n");
    }

    #endregion

    #region Strict CommandApp harness

    /// <summary>
    /// Runs the stub command through a fresh strict-configured CommandApp and returns
    /// its exit code together with whatever Spectre rendered.
    /// </summary>
    /// <remarks>
    /// Both swaps below touch process-global console state, so the body is routed
    /// through <see cref="ProcessConsoleCapture.Run{T}(Func{T})"/> — the same
    /// serialization gate every other capture in the suite uses. Ungated, a concurrent
    /// capture rebinding <see cref="AnsiConsole.Console"/> mid-run would either pull
    /// this run's renders into its own writer (empty capture) or have its own capture
    /// contaminated by the forwarding writer. The shared-console binding stays intact:
    /// the callback re-pins the static to <see cref="SharedRenderConsole.Console"/> so
    /// every render keeps flowing through the forwarding-writer channel, and
    /// <see cref="BindSharedRenderConsole"/> still performs the one-time binding at
    /// assembly load before any test runs.
    /// </remarks>
    private static (int ExitCode, string Output) RunThroughStrictApp(string[] arguments)
    {
        return Capture(() =>
        {
            SquadAppRegistrar registrar = new();
            CommandApp app = new(registrar);
            app.Configure(config =>
            {
                config.SetApplicationName("kyber-weave");
                config.UseStrictParsing();
                config.AddCommand<PathBindingStubCommand>("install");
            });

            return app.Run(arguments);
        });
    }

    /// <summary>Runs <paramref name="execute"/> once with console output captured.</summary>
    private static (T Result, string Output) Capture<T>(Func<T> execute)
    {
        CapturedConsoleExecution<T> execution = ProcessConsoleCapture.Run(execute);
        return (execution.Result, execution.Output);
    }

    private static async Task<(T Result, string Output)> CaptureAsync<T>(Func<Task<T>> execute)
    {
        CapturedConsoleExecution<T> execution = await ProcessConsoleCapture.RunAsync(execute);
        return (execution.Result, execution.Output);
    }

    private string[] Files(string directory) =>
        [.. _state.Files.EnumerateFiles(directory, "*", recursive: true)];

    private static string Normalize(string output) => string.Join(
        ' ',
        output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Seeds an existing project-scope deployment — deployed file, ownership receipt, and
    /// lock — after the fixture pattern SquadCliCommandTests uses, so the update and
    /// uninstall wiring tests satisfy the receipt prerequisites that sit before their
    /// confirmation gates.
    /// </summary>
    private void SeedDeployment(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadStateStore stateStore,
        params (string RelativePath, string Content)[] files)
    {
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, scope);
        string lockPath = stateStore.ResolveLockPath(targetRoot, scope);
        _state.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relativePath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relativePath);
            _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            _state.Files.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ownedFiles.Add(new SquadOwnedFile(relativePath, sha256, "codex", Adopted: false));
        }

        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v1",
            Scope: scope,
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

    /// <summary>
    /// Seeds an existing global-scope deployment whose receipt names a file per target, so
    /// a verb that derives its targets from the receipt (uninstall has no --target option)
    /// is seen owning files beneath more than one target's physical global root. The state
    /// files land under the store's global roots directory keyed on the anchor's physical
    /// identity, exactly where a real global deployment's state lives.
    /// </summary>
    private void SeedGlobalDeployment(
        string anchorRoot,
        SquadStateStore stateStore,
        params (string RelativePath, string Content, string Target)[] files)
    {
        string receiptPath = stateStore.ResolveReceiptPath(anchorRoot, SquadDeploymentScope.Global);
        string lockPath = stateStore.ResolveLockPath(anchorRoot, SquadDeploymentScope.Global);
        _state.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relativePath, string content, string target) in files)
        {
            ownedFiles.Add(new SquadOwnedFile(
                relativePath,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                target,
                Adopted: false));
        }

        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Global,
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
            Targets: [.. files.Select(file => file.Target).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Exclusions: [],
            Translation: "best-effort",
            BundleDigest: "a".PadRight(64, '0'),
            AssetDigest: "b".PadRight(64, '0'),
            Apm: new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));

        _state.Files.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt));
        _state.Files.WriteAllText(lockPath, stateStore.SerializeLock(squadLock));
    }

    /// <summary>
    /// Stub over the real settings type that records the instance the parser bound, so
    /// the assertion inspects what an operator's argv actually produced.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1812: An internal class that is apparently never instantiated",
        Justification = "Spectre.Console.Cli's type resolver instantiates the stub via reflection.")]
    private sealed class PathBindingStubCommand : Command<SquadInstallSettings>
    {
        internal static SquadInstallSettings? Captured { get; set; }
        public override int Execute(CommandContext context, SquadInstallSettings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    /// <summary>
    /// Registrar for the strict-parse app: pins the active console so every render
    /// routes through the active test capture, and resolves everything else the way
    /// the default registrar does.
    /// </summary>
    private sealed class SquadAppRegistrar : ITypeRegistrar
    {
        private readonly Dictionary<Type, object> _instances = new() { [typeof(IAnsiConsole)] = AnsiConsole.Console };
        private readonly Dictionary<Type, List<Type>> _implementations = [];

        public void Register(Type service, Type implementation)
        {
            if (!_implementations.TryGetValue(service, out List<Type>? known))
            {
                known = [];
                _implementations[service] = known;
            }

            known.Add(implementation);
        }

        public void RegisterInstance(Type service, object instance) => _instances[service] = instance;

        public void RegisterLazy(Type service, Func<object> factory)
        {
            // The pinned render console must not be replaced by the lazily registered
            // default console: later renders would land in a disposed capture's writer.
            if (!_instances.ContainsKey(service))
            {
                _instances[service] = factory();
            }
        }

        public ITypeResolver Build() => new SquadAppResolver(_instances, _implementations);
    }

    private sealed class SquadAppResolver(
        Dictionary<Type, object> instances,
        Dictionary<Type, List<Type>> implementations) : ITypeResolver
    {
        public object? Resolve(Type? type)
        {
            if (type is null)
            {
                return null;
            }

            if (instances.TryGetValue(type, out object? instance))
            {
                return instance;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                Type elementType = type.GetGenericArguments()[0];
                List<object> built = implementations
                    .SelectMany(pair => pair.Value)
                    .Where(elementType.IsAssignableFrom)
                    .Select(implementation => Activator.CreateInstance(implementation))
                    .Where(resolved => resolved is not null)
                    .Select(resolved => resolved!)
                    .ToList();
                Array typed = Array.CreateInstance(elementType, built.Count);
                for (int index = 0; index < built.Count; index++)
                {
                    typed.SetValue(built[index], index);
                }

                return typed;
            }

            if (implementations.TryGetValue(type, out List<Type>? candidates) && candidates.Count > 0)
            {
                return Activator.CreateInstance(candidates[^1]);
            }

            try
            {
                return Activator.CreateInstance(type);
            }
            catch (Exception ex) when (ex is MissingMemberException or ArgumentException or MemberAccessException or NotSupportedException or TargetInvocationException)
            {
                return null;
            }
        }
    }

    #endregion
}
