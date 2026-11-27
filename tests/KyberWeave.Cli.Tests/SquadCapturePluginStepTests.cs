using KyberWeave.Testing.Squad;
using System.Diagnostics;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests;

/// <summary>
///     Squad's global install and update run the KyberDash OpenCode capture-plugin verb after a successful
///     deployment. These tests use a fake process executor and a fake KyberDash locator, so no test ever starts
///     a real <c>kyberdash</c> on the developer's machine. The in-memory state roots every fake home and global root.
/// </summary>
public sealed class SquadCapturePluginStepTests
{
    private const string FakeKyberDash = "/fake/bin/kyberdash";
    private const string FakeOpenCodeRoot = "/fake/opencode";
    private const string InstalledVersion = "1.2.3";
    private const string UpdatedVersion = "1.2.4";

    // The verb's guidance lines are every stdout line that starts with this prefix (D22). The copy after the
    // prefix belongs to kyberDash, so these tests use neutral text and never pin its wording.
    private const string HarnessPrefix = "harness opencode: ";
    private const string GuidanceAlpha = "guidance from the verb, line alpha";
    private const string GuidanceBeta = "guidance from the verb, line beta";

    // ---- the step itself ----

    [Fact]
    public void RunInvokesThePluginVerbWithPreserveAndTheResolvedConfigDir()
    {
        FakeProcessExecutor executor = new();
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out _);

        Assert.Equal(CapturePluginStepOutcome.Completed, outcome);
        ProcessStartInfo call = Assert.Single(executor.Calls);
        Assert.Equal(FakeKyberDash, call.FileName);
        Assert.Equal(
            [
                "kyber", "capture", "plugin", "install", "--harness", "opencode", "--preserve", "--config-dir",
                FakeOpenCodeRoot
            ],
            call.ArgumentList);
    }

    [Fact]
    public void RunPassesDryRunThroughToTheVerb()
    {
        FakeProcessExecutor executor = new();
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        Capture(() => step.Run(FakeOpenCodeRoot, true), out _);

        ProcessStartInfo call = Assert.Single(executor.Calls);
        Assert.Equal(
            [
                "kyber", "capture", "plugin", "install", "--harness", "opencode", "--preserve", "--config-dir",
                FakeOpenCodeRoot, "--dry-run"
            ],
            call.ArgumentList);
    }

    [Fact]
    public void RunWithoutKyberDashSkipsWithoutStartingAnything()
    {
        FakeProcessExecutor executor = new();
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => null);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Skipped, outcome);
        Assert.Empty(executor.Calls);
        Assert.Contains("skipped", output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave update", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The step prints the verb's own guidance line for a changed plugin, and for a kept one when the verb sends
    ///     one. The restart wording is kyberDash's (ADR 0031), so Squad must not print a line of its own.
    /// </summary>
    [Theory]
    [InlineData("installed")]
    [InlineData("upgraded")]
    [InlineData("kept-edited")]
    public void RunRelaysTheVerbsHarnessGuidanceForEveryPluginVerdict(string verdict)
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            $"opencode plugin: {verdict} /fake/opencode/plugins/kyberdash-opencode.js\n{HarnessPrefix}{GuidanceAlpha}\n");
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Completed, outcome);
        Assert.Contains($"opencode plugin: {verdict}", output, StringComparison.Ordinal);
        Assert.Contains(GuidanceAlpha, output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every harness line the verb prints is relayed, not only the first one. The step used to print a single
    ///     verdict line, so a second guidance line was lost.
    /// </summary>
    [Fact]
    public void RunRelaysEveryHarnessOpenCodeLineThePluginVerbPrints()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            "opencode plugin: installed /fake/opencode/plugins/kyberdash-opencode.js\n" +
            $"{HarnessPrefix}{GuidanceAlpha}\n" +
            $"{HarnessPrefix}{GuidanceBeta}\n");
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Contains(GuidanceAlpha, output, StringComparison.Ordinal);
        Assert.Contains(GuidanceBeta, output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The hard-coded "Restart OpenCode" line was the step's own wording, which drifted from kyberDash's. No
    ///     output line may begin with it, whatever the verdict.
    /// </summary>
    [Theory]
    [InlineData("installed")]
    [InlineData("upgraded")]
    [InlineData("kept-edited")]
    public void RunNeverPrintsAHardCodedRestartOpenCodeLine(string verdict)
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            $"opencode plugin: {verdict} /fake/opencode/plugins/kyberdash-opencode.js\n{HarnessPrefix}{GuidanceAlpha}\n");
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.DoesNotContain(
            output.Split('\n'),
            line => line.StartsWith("Restart OpenCode", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Only lines that start with the harness prefix are guidance. Verb chatter, and a line that merely mentions
    ///     the prefix, are not relayed. A step that dumped all of stdout would fail this.
    /// </summary>
    [Fact]
    public void RunDoesNotRelayStdoutLinesWithoutTheHarnessPrefix()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            "opencode plugin: installed /fake/opencode/plugins/kyberdash-opencode.js\n" +
            $"{HarnessPrefix}{GuidanceAlpha}\n" +
            "verb chatter that is not guidance\n" +
            "verb note: harness opencode: mid-line mention\n");
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Completed, outcome);
        Assert.DoesNotContain("verb chatter that is not guidance", output, StringComparison.Ordinal);
        Assert.DoesNotContain("mid-line mention", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunReportsAKeptPluginWithoutTheRestartLine()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            "opencode plugin: kept-edited /fake/opencode/plugins/kyberdash-opencode.js\n");
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Completed, outcome);
        Assert.Contains("kept-edited", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart OpenCode", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTreatsAnUnknownCommandAsANamedSkipNotAFailure()
    {
        // An older KyberDash without the verb exits non-zero with commander's unknown-command message.
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            "error: unknown command 'capture'\n",
            1);
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Skipped, outcome);
        Assert.Contains("predates", output, StringComparison.Ordinal);
        Assert.DoesNotContain("warning", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunReportsANonZeroExitAsAWarningAndNamesTheExitCode()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput("kyberdash", string.Empty, 3);
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Warned, outcome);
        Assert.Contains("warning", output, StringComparison.Ordinal);
        Assert.Contains("exit 3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunWhenTheProcessCannotStartIsAWarningNotAnException()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithFailure("kyberdash", "cannot start kyberdash");
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Warned, outcome);
        Assert.Contains("warning", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunWhenTheProcessIsNotExecutableIsAWarningNotAnException()
    {
        // chmod -x on kyberdash: the start is refused with an access error, not a missing file.
        FakeProcessExecutor executor = new FakeProcessExecutor().WithException(
            "kyberdash",
            new UnauthorizedAccessException("Access to the path '/fake/bin/kyberdash' is denied."));
        KyberDashCapturePluginStep step = SquadTestCommands.CapturePluginStep(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Warned, outcome);
        Assert.Contains("warning", output, StringComparison.Ordinal);
        Assert.Contains("could not run KyberDash", output, StringComparison.Ordinal);
    }

    // ---- wiring through the Squad commands ----

    // Every install case below runs the install command over its own in-memory state, with the plugin step's
    // behaviour supplied by the executor its spec names. A test reads the exit code, console output and plugin-step
    // calls. The update cases install first, then run their update for real over what that install left.

    [Fact]
    public void GlobalInstallWithOpenCodeRunsThePluginStepOnceAfterSuccess()
    {
        InstallRun prepared = RunInstall(OpenCodeGlobal());

        Assert.Equal(0, prepared.Record.ExitCode);
        ProcessStartInfo call = Assert.Single(prepared.Calls);
        Assert.Equal(
            [
                "kyber", "capture", "plugin", "install", "--harness", "opencode", "--preserve", "--config-dir",
                prepared.GlobalRoot(SquadTarget.OpenCode)
            ],
            call.ArgumentList);
    }

    [Fact]
    public void GlobalInstallWithoutOpenCodeDoesNotRunThePluginStep()
    {
        InstallRun prepared = RunInstall(new InstallSpec("codex-global", ["codex"], SquadDeploymentScope.Global));

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    [Fact]
    public void GlobalInstallWithNoCapturePluginDoesNotRunThePluginStep()
    {
        InstallRun prepared = RunInstall(OpenCodeGlobal() with { NoCapturePlugin = true });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    [Fact]
    public void GlobalInstallDryRunPassesDryRunThroughToThePluginStep()
    {
        InstallRun prepared = RunInstall(OpenCodeGlobal() with { DryRun = true });

        Assert.Equal(0, prepared.Record.ExitCode);
        ProcessStartInfo call = Assert.Single(prepared.Calls);
        Assert.Equal("--dry-run", call.ArgumentList[^1]);
    }

    [Fact]
    public void ProjectInstallWithOpenCodeDoesNotRunThePluginStep()
    {
        // The plugin is global capture, so a project-scope install never touches the OpenCode global root.
        InstallRun prepared = RunInstall(new InstallSpec(
            "opencode-project",
            ["opencode"],
            SquadDeploymentScope.Project));

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    [Fact]
    public void DeclinedGlobalInstallDoesNotRunThePluginStep()
    {
        InstallRun prepared = RunInstall(OpenCodeGlobal() with { Declined = true });

        Assert.Equal(2, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    [Fact]
    public void PluginStepFailureLeavesTheSquadExitCodeUnchanged()
    {
        InstallRun prepared = RunInstall(OpenCodeGlobal() with { Executor = "exit-3" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("Successfully installed", prepared.Record.Output, StringComparison.Ordinal);
        Assert.Contains("warning", prepared.Record.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginStepThatCannotBeExecutedLeavesTheSquadExitCodeUnchanged()
    {
        InstallRun prepared = RunInstall(OpenCodeGlobal() with { Executor = "not-executable" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("Successfully installed", prepared.Record.Output, StringComparison.Ordinal);
        Assert.Contains("warning", prepared.Record.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUpdateWithOpenCodeRunsThePluginStepAfterTheDeploymentUpdates()
    {
        using Harness h = new();
        InstallOpenCodeGlobal(h);
        FakeProcessExecutor executor = new();

        CommandExecution execution = Capture(() => h.UpdateCommand(executor).Execute(null!, GlobalUpdate(h)), out _);

        Assert.Equal(0, execution.ExitCode);
        ProcessStartInfo call = Assert.Single(executor.Calls);
        Assert.Equal(h.GlobalRoot(SquadTarget.OpenCode), call.ArgumentList[^1]);
    }

    [Fact]
    public void GlobalUpdateWithNoCapturePluginDoesNotRunThePluginStep()
    {
        using Harness h = new();
        InstallOpenCodeGlobal(h);
        FakeProcessExecutor executor = new();
        SquadUpdateSettings settings = GlobalUpdate(h);
        settings.NoCapturePlugin = true;

        CommandExecution execution = Capture(() => h.UpdateCommand(executor).Execute(null!, settings), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void GlobalInstallRelaysTheVerbGuidanceAndKeepsTheSquadExitCode()
    {
        InstallRun prepared = RunInstall(OpenCodeGlobal() with { Executor = "guidance" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains(GuidanceAlpha, prepared.Record.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUpdateRelaysTheVerbGuidanceAndKeepsTheSquadExitCode()
    {
        using Harness h = new();
        InstallOpenCodeGlobal(h);
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            $"opencode plugin: upgraded /fake/opencode/plugins/kyberdash-opencode.js\n{HarnessPrefix}{GuidanceAlpha}\n");

        CommandExecution execution = Capture(() => h.UpdateCommand(executor).Execute(null!, GlobalUpdate(h)), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(GuidanceAlpha, execution.Output, StringComparison.Ordinal);
    }

    // ---- the Factory (Droid) capture plugin ----

    /// <summary>
    ///     A global Factory deployment installs the Kyber-owned capture plugin for Factory through the
    ///     KyberDash verb, in preserve mode. The call names no root: the verb resolves the Factory
    ///     harness's own root itself.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryRunsThePluginVerbForFactoryInPreserveMode()
    {
        InstallRun prepared = RunInstall(FactoryGlobal());

        Assert.Equal(0, prepared.Record.ExitCode);
        ProcessStartInfo call = Assert.Single(prepared.Calls);
        Assert.Equal(FakeKyberDash, call.FileName);
        Assert.Equal(
            [
                "kyber", "capture", "plugin", "install", "--harness", "factory", "--preserve"
            ],
            call.ArgumentList);
    }

    /// <summary>
    ///     The Factory call names no root at all. The verb resolves the Factory harness's own root
    ///     (<c>$FACTORY_DIR</c>, else <c>~/.factory</c>) — the same tree the Factory FILE source reads
    ///     — so the plugin and the file source always pair from one root, and the deploy hands the
    ///     verb no directory of its own.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryNamesNoRootSoTheVerbResolvesTheFactoryTreeItself()
    {
        InstallRun prepared = RunInstall(FactoryGlobal() with { FactoryHomeRoots = true });

        Assert.Equal(0, prepared.Record.ExitCode);
        // The repository resolves the Factory tree to ~/.factory: the tree the verb resolves for itself.
        Assert.Equal(Path.Combine(prepared.FactoryHome!, ".factory"), prepared.GlobalRoot(SquadTarget.Factory));
        ProcessStartInfo call = Assert.Single(prepared.Calls);
        Assert.Equal(string.Empty, ConfigDirOf(call));
        Assert.DoesNotContain("--config-dir", call.ArgumentList);
        Assert.DoesNotContain(prepared.GlobalRoot(SquadTarget.Factory), call.ArgumentList);
    }

    /// <summary>
    ///     The two steps never share a root: the OpenCode plugin lives under the OpenCode global root
    ///     and keeps its <c>--config-dir</c>, while the Factory step names no root at all, so it can
    ///     never be handed the OpenCode one.
    /// </summary>
    [Fact]
    public void GlobalInstallWithOpenCodeAndFactoryGivesOnlyTheOpenCodeStepARoot()
    {
        InstallRun prepared = RunInstall(OpenCodeAndFactoryGlobal());

        Assert.Equal(0, prepared.Record.ExitCode);
        IReadOnlyList<ProcessStartInfo> calls = prepared.Calls;
        Assert.Equal(2, calls.Count);
        // The OpenCode step keeps its own root: that flag still selects the OpenCode config root.
        Assert.Equal(prepared.GlobalRoot(SquadTarget.OpenCode), ConfigDirOf(calls[0]));
        // The Factory step names no root, so it cannot be handed the OpenCode one.
        Assert.Equal(string.Empty, ConfigDirOf(calls[1]));
        Assert.DoesNotContain(prepared.GlobalRoot(SquadTarget.OpenCode), calls[1].ArgumentList);
        Assert.NotEqual(prepared.GlobalRoot(SquadTarget.OpenCode), prepared.GlobalRoot(SquadTarget.Factory));
        // The order is unchanged: OpenCode first, Factory second.
        Assert.Equal("opencode", HarnessOf(calls[0]));
        Assert.Equal("factory", HarnessOf(calls[1]));
    }

    /// <summary>
    ///     The Factory plugin step runs after the OpenCode one: the step that already existed keeps its
    ///     place in the deploy sequence and the new one follows it.
    /// </summary>
    [Fact]
    public void GlobalInstallWithOpenCodeAndFactoryRunsTheFactoryPluginStepAfterTheOpenCodeOne()
    {
        InstallRun prepared = RunInstall(OpenCodeAndFactoryGlobal());

        Assert.Equal(0, prepared.Record.ExitCode);
        IReadOnlyList<ProcessStartInfo> calls = prepared.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal("opencode", HarnessOf(calls[0]));
        Assert.Equal("factory", HarnessOf(calls[1]));
    }

    /// <summary>
    ///     The plugin is global capture, so a project-scope deployment never touches the Factory global
    ///     root and never runs the Factory plugin step.
    /// </summary>
    [Fact]
    public void ProjectInstallWithFactoryDoesNotRunThePluginStep()
    {
        InstallRun prepared = RunInstall(new InstallSpec(
            "factory-project",
            ["factory"],
            SquadDeploymentScope.Project));

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    /// <summary>
    ///     A global deployment that does not include Factory runs the OpenCode plugin step and nothing
    ///     else: the Factory plugin is installed only for a Factory target.
    /// </summary>
    [Fact]
    public void GlobalInstallWithoutFactoryRunsOnlyTheOpenCodePluginStep()
    {
        InstallRun prepared = RunInstall(OpenCodeGlobal());

        Assert.Equal(0, prepared.Record.ExitCode);
        ProcessStartInfo call = Assert.Single(prepared.Calls);
        Assert.Equal("opencode", HarnessOf(call));
    }

    /// <summary>
    ///     A missing KyberDash is a skip, not a failure: the deployment that already succeeded keeps its
    ///     exit code and nothing is started.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryAndNoKyberDashSkipsThePluginStepAndKeepsTheExitCode()
    {
        InstallRun prepared = RunInstall(FactoryGlobal() with { MissingKyberDash = true });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
        Assert.Contains("capture plugin", prepared.Record.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skipped", prepared.Record.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A refused Factory install is a warning, not a failed deployment: the capture plugin is an
    ///     optional surface and the owner can rerun the printed command.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryKeepsTheSquadExitCodeWhenThePluginStepFails()
    {
        InstallRun prepared = RunInstall(FactoryGlobal() with { Executor = "exit-3" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("Successfully installed", prepared.Record.Output, StringComparison.Ordinal);
        Assert.Contains("exit 3", prepared.Record.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The step relays the verb's own verdict line. The wording is kyberDash's, so the line carries a
    ///     neutral marker and this test never pins what KyberDash actually says.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryRelaysTheVerbsVerdictLine()
    {
        InstallRun prepared = RunInstall(FactoryGlobal() with { Executor = "verdict" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("FACTORY-VERDICT-LINE", prepared.Record.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A KyberDash that predates the capture plugin command is a named skip, not a warning: the
    ///     deployment must not report a failure it cannot fix.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryNamesAPreVerbKyberDashAsASkipAndNotAWarning()
    {
        InstallRun prepared = RunInstall(FactoryGlobal() with { Executor = "old-dash" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("capture plugin", prepared.Record.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "capture plugin step failed",
            prepared.Record.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The harness the verb names, or an empty string when it names none.</summary>
    private static string HarnessOf(ProcessStartInfo call)
    {
        return HarnessOf(call.ArgumentList);
    }

    private static string HarnessOf(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index + 1 < arguments.Count; index++)
            if (arguments[index] == "--harness")
                return arguments[index + 1];

        return string.Empty;
    }

    /// <summary>The root the verb was given, or an empty string when it was given none.</summary>
    private static string ConfigDirOf(ProcessStartInfo call)
    {
        for (int index = 0; index + 1 < call.ArgumentList.Count; index++)
            if (call.ArgumentList[index] == "--config-dir")
                return call.ArgumentList[index + 1];

        return string.Empty;
    }

    // ---- fixtures ----

    /// <summary>One install input: every field that changes the run.</summary>
    private sealed record InstallSpec(
        string Recipe,
        string[] Targets,
        SquadDeploymentScope Scope,
        string Version = InstalledVersion,
        string Executor = "ok",
        bool NoCapturePlugin = false,
        bool DryRun = false,
        bool Declined = false,
        bool MissingKyberDash = false,
        bool FactoryHomeRoots = false);

    private static InstallSpec OpenCodeGlobal() =>
        new("opencode-global", ["opencode"], SquadDeploymentScope.Global);

    private static InstallSpec FactoryGlobal() =>
        new("factory-global", ["factory"], SquadDeploymentScope.Global);

    private static InstallSpec OpenCodeAndFactoryGlobal() =>
        new("opencode-factory-global", ["opencode", "factory"], SquadDeploymentScope.Global);

    /// <summary>
    /// Installs the OpenCode global deployment into <paramref name="harness"/> and checks that the install
    /// succeeded, so a passing update cannot come from a seed that wrote nothing.
    /// </summary>
    private static void InstallOpenCodeGlobal(Harness harness)
    {
        FakeProcessExecutor executor = new();
        SquadInstallSettings settings = GlobalInstall(harness, ["opencode"]);

        CommandExecution execution = Capture(() => harness.InstallCommand(executor).Execute(null!, settings), out _);

        Assert.Equal(0, execution.ExitCode);
    }

    /// <summary>Runs the install command for <paramref name="spec"/> over its own in-memory state.</summary>
    private static InstallRun RunInstall(InstallSpec spec)
    {
        string? factoryHome = null;
        Harness? created = null;
        try
        {
            if (spec.FactoryHomeRoots)
            {
                created = new Harness(factoryHomeRoots: true);
                factoryHome = created.FactoryHome;
            }
            else
            {
                created = new Harness();
            }

            Harness h = created;
            FakeProcessExecutor executor = ExecutorFor(spec.Executor);
            SquadInstallSettings settings = spec.Scope == SquadDeploymentScope.Global
                ? GlobalInstall(h, spec.Targets, spec.Version)
                : ProjectInstall(h, spec.Targets, spec.Version);
            settings.NoCapturePlugin = spec.NoCapturePlugin;
            settings.DryRun = spec.DryRun;
            settings.Yes = !spec.Declined;

            CommandExecution execution = Capture(
                () => (spec.Declined
                        ? h.InstallCommand(executor, isInteractive: true, readAnswer: _ => false)
                        : h.InstallCommand(
                            executor,
                            locateKyberDash: spec.MissingKyberDash ? () => null : null))
                    .Execute(null!, settings),
                out _);

            InstallRun run = new(
                execution,
                executor.Calls,
                SquadTargetCatalog.Parse(spec.Targets).ToDictionary(target => target, target => h.GlobalRoot(target)),
                factoryHome);
            return run;
        }
        finally
        {
            created?.Dispose();
        }
    }

    private static FakeProcessExecutor ExecutorFor(string behavior)
    {
        return behavior switch
        {
            "ok" => new FakeProcessExecutor(),
            "exit-3" => new FakeProcessExecutor().WithProbeOutput("kyberdash", string.Empty, 3),
            "not-executable" => new FakeProcessExecutor().WithException(
                "kyberdash",
                new UnauthorizedAccessException("Access to the path '/fake/bin/kyberdash' is denied.")),
            "guidance" => new FakeProcessExecutor().WithProbeOutput(
                "kyberdash",
                $"opencode plugin: installed /fake/opencode/plugins/kyberdash-opencode.js\n" +
                $"{HarnessPrefix}{GuidanceAlpha}\n"),
            "verdict" => new FakeProcessExecutor().WithProbeOutput("kyberdash", "FACTORY-VERDICT-LINE\n"),
            "old-dash" => new FakeProcessExecutor().WithProbeOutput(
                "kyberdash",
                "error: unknown command 'capture'\n",
                1),
            _ => throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "Unknown executor behavior.")
        };
    }

    private static CommandExecution Capture(Func<int> execute, out string output)
    {
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(execute);
        output = captured.Output;
        return new CommandExecution(captured.Result, captured.Output, captured.ErrorOutput);
    }

    private static CapturePluginStepOutcome Capture(Func<CapturePluginStepOutcome> step, out string output)
    {
        CapturedConsoleExecution<CapturePluginStepOutcome> captured = ProcessConsoleCapture.Run(step);
        output = captured.Output;
        return captured.Result;
    }

    private static SquadInstallSettings GlobalInstall(Harness h, string[] targets, string version = InstalledVersion)
    {
        return new SquadInstallSettings
        {
            Path = h.Anchor,
            Global = true,
            Targets = targets,
            Version = version,
            Yes = true
        };
    }

    private static SquadInstallSettings ProjectInstall(Harness h, string[] targets, string version)
    {
        return new SquadInstallSettings
        {
            Path = h.MakeDirectory("project"),
            Global = false,
            Targets = targets,
            Version = version,
            Yes = true
        };
    }

    private static SquadUpdateSettings GlobalUpdate(Harness h)
    {
        return new SquadUpdateSettings
        {
            Path = h.Anchor,
            Global = true,
            Version = UpdatedVersion,
            Yes = true
        };
    }

    private sealed record CommandExecution(int ExitCode, string Output, string ErrorOutput);

    /// <summary>One install run: the command's result, the plugin-step calls it made, and the roots a test names when it checks them.</summary>
    private sealed class InstallRun(
        CommandExecution record,
        IReadOnlyList<ProcessStartInfo> calls,
        IReadOnlyDictionary<SquadTarget, string> globalRootByTarget,
        string? factoryHome)
    {
        /// <summary>The command's exit code and output.</summary>
        public CommandExecution Record { get; } = record;

        /// <summary>The plugin-step calls the command made, in order.</summary>
        public IReadOnlyList<ProcessStartInfo> Calls { get; } = calls;

        /// <summary>The home a Factory-only resolver resolved <c>~/.factory</c> against, or null.</summary>
        public string? FactoryHome { get; } = factoryHome;

        /// <summary>The harness's global root for <paramref name="target"/>, as it resolved them.</summary>
        public string GlobalRoot(SquadTarget target) => globalRootByTarget[target];
    }

    private sealed class Harness : IDisposable
    {
        private readonly ISquadGlobalRootResolver _globalRoots;
        private readonly FakeSquadReleaseSource _releaseSource;
        private readonly InMemorySquadState _state = new();

        public Harness(bool factoryHomeRoots = false)
        {
            string root = Path.Combine(_state.Files.Root, "harness");
            Anchor = Path.Combine(root, "global-anchor");
            GlobalRootsDirectory = Path.Combine(root, "global-roots");
            _state.Files.CreateDirectory(Anchor);
            _state.Files.CreateDirectory(GlobalRootsDirectory);
            if (factoryHomeRoots)
            {
                FactoryHome = Path.Combine(root, "factory-home");
                _state.Files.CreateDirectory(FactoryHome);
            }

            _globalRoots = FactoryHome is null
                ? new PerTargetGlobalRoots(GlobalRootsDirectory)
                : new SquadGlobalRoots(_ => null, FactoryHome);
            string source = Path.Combine(_state.Files.Root, "release", "kyber-squad");
            _ = CanonicalSquadTree.CopyInto(_state.Files, source);
            _releaseSource = FakeSquadReleaseSource.FromDirectory(source, _state.Files);
        }

        public string Anchor { get; }

        public string GlobalRootsDirectory { get; }

        public string? FactoryHome { get; }

        public void Dispose()
        {
            _releaseSource.Dispose();
        }

        public string GlobalRoot(SquadTarget target)
        {
            return _globalRoots.ResolveGlobalRoot(target);
        }

        public string MakeDirectory(string name)
        {
            string path = Path.Combine(Path.GetDirectoryName(Anchor)!, name);
            _state.Files.CreateDirectory(path);
            return path;
        }

        public SquadInstallCommand InstallCommand(
            IProcessExecutor executor,
            bool isInteractive = false,
            Func<string, bool>? readAnswer = null,
            Func<string?>? locateKyberDash = null)
        {
            return SquadTestCommands.InMemoryInstall(
                _state,
                _releaseSource,
                FakeSquadRenderer.ScriptedAgents("architect"),
                globalRoots: _globalRoots,
                isInteractive: isInteractive,
                readAnswer: readAnswer ?? (_ => true),
                capturePluginStep: PluginStep(executor, locateKyberDash));
        }

        public SquadUpdateCommand UpdateCommand(IProcessExecutor executor)
        {
            return SquadTestCommands.InMemoryUpdate(
                _state,
                _releaseSource,
                FakeSquadRenderer.ScriptedAgents("architect"),
                globalRoots: _globalRoots,
                isInteractive: false,
                readAnswer: _ => true,
                capturePluginStep: PluginStep(executor));
        }

        private static KyberDashCapturePluginStep PluginStep(
            IProcessExecutor executor,
            Func<string?>? locateKyberDash = null)
        {
            return SquadTestCommands.CapturePluginStep(executor, locateKyberDash ?? (() => FakeKyberDash));
        }
    }

    private sealed class PerTargetGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return Path.Combine(root, SquadTargetCatalog.GetToken(target));
        }
    }
}
