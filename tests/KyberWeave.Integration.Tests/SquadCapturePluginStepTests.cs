using System.Diagnostics;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
///     Squad's global install and update run the KyberDash OpenCode capture-plugin verb after a successful
///     deployment. These tests use a fake process executor and a fake KyberDash locator, so no test ever starts
///     a real <c>kyberdash</c> on the developer's machine. The temp directory roots every fake home and global root.
/// </summary>
public sealed class SquadCapturePluginStepTests : IDisposable
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

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    // ---- the step itself ----

    [Fact]
    public void RunInvokesThePluginVerbWithPreserveAndTheResolvedConfigDir()
    {
        FakeProcessExecutor executor = new();
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

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
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

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
        KyberDashCapturePluginStep step = new(executor, () => null);

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
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

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
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

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
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

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
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

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
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

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
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Skipped, outcome);
        Assert.Contains("predates", output, StringComparison.Ordinal);
        Assert.DoesNotContain("warning", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunReportsANonZeroExitAsAWarningAndNamesTheExitCode()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput("kyberdash", string.Empty, 3);
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Warned, outcome);
        Assert.Contains("warning", output, StringComparison.Ordinal);
        Assert.Contains("exit 3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunWhenTheProcessCannotStartIsAWarningNotAnException()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithFailure("kyberdash", "cannot start kyberdash");
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

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
        KyberDashCapturePluginStep step = new(executor, () => FakeKyberDash);

        CapturePluginStepOutcome outcome = Capture(() => step.Run(FakeOpenCodeRoot, false), out string output);

        Assert.Equal(CapturePluginStepOutcome.Warned, outcome);
        Assert.Contains("warning", output, StringComparison.Ordinal);
        Assert.Contains("could not run KyberDash", output, StringComparison.Ordinal);
    }

    // ---- wiring through the Squad commands ----

    // Every install case below is one prepared install, built once for the run through
    // PreparedSquadInstalls and keyed by everything that changes what the command or the plugin step
    // does. A test reads the recorded exit code, console output and plugin-step calls. The three
    // update cases still run their update for real: each updates the deployment that a copy of the
    // prepared OpenCode global install left in its own home.

    [Fact]
    public void GlobalInstallWithOpenCodeRunsThePluginStepOnceAfterSuccess()
    {
        PreparedCase prepared = Prepared(OpenCodeGlobal());

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
        PreparedCase prepared = Prepared(new InstallSpec("codex-global", ["codex"], SquadDeploymentScope.Global));

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    [Fact]
    public void GlobalInstallWithNoCapturePluginDoesNotRunThePluginStep()
    {
        PreparedCase prepared = Prepared(OpenCodeGlobal() with { NoCapturePlugin = true });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    [Fact]
    public void GlobalInstallDryRunPassesDryRunThroughToThePluginStep()
    {
        PreparedCase prepared = Prepared(OpenCodeGlobal() with { DryRun = true });

        Assert.Equal(0, prepared.Record.ExitCode);
        ProcessStartInfo call = Assert.Single(prepared.Calls);
        Assert.Equal("--dry-run", call.ArgumentList[^1]);
    }

    [Fact]
    public void ProjectInstallWithOpenCodeDoesNotRunThePluginStep()
    {
        // The plugin is global capture, so a project-scope install never touches the OpenCode global root.
        PreparedCase prepared = Prepared(new InstallSpec(
            "opencode-project",
            ["opencode"],
            SquadDeploymentScope.Project));

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    [Fact]
    public void DeclinedGlobalInstallDoesNotRunThePluginStep()
    {
        PreparedCase prepared = Prepared(OpenCodeGlobal() with { Declined = true });

        Assert.Equal(2, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
    }

    [Fact]
    public void PluginStepFailureLeavesTheSquadExitCodeUnchanged()
    {
        PreparedCase prepared = Prepared(OpenCodeGlobal() with { Executor = "exit-3" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("Successfully installed", prepared.Record.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("warning", prepared.Record.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginStepThatCannotBeExecutedLeavesTheSquadExitCodeUnchanged()
    {
        PreparedCase prepared = Prepared(OpenCodeGlobal() with { Executor = "not-executable" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("Successfully installed", prepared.Record.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("warning", prepared.Record.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUpdateWithOpenCodeRunsThePluginStepAfterTheDeploymentUpdates()
    {
        using Harness h = new(Path.Combine(_temp.Path, "update-opencode"));
        CopyOpenCodeGlobal(h);
        FakeProcessExecutor executor = new();

        CommandExecution execution = Capture(() => h.UpdateCommand(executor).Execute(null!, GlobalUpdate(h)), out _);

        Assert.Equal(0, execution.ExitCode);
        ProcessStartInfo call = Assert.Single(executor.Calls);
        Assert.Equal(h.GlobalRoot(SquadTarget.OpenCode), call.ArgumentList[^1]);
    }

    [Fact]
    public void GlobalUpdateWithNoCapturePluginDoesNotRunThePluginStep()
    {
        using Harness h = new(Path.Combine(_temp.Path, "update-no-plugin"));
        CopyOpenCodeGlobal(h);
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
        PreparedCase prepared = Prepared(OpenCodeGlobal() with { Executor = "guidance" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains(GuidanceAlpha, prepared.Record.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUpdateRelaysTheVerbGuidanceAndKeepsTheSquadExitCode()
    {
        using Harness h = new(Path.Combine(_temp.Path, "update-guidance"));
        CopyOpenCodeGlobal(h);
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
        PreparedCase prepared = Prepared(FactoryGlobal());

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
        PreparedCase prepared = Prepared(FactoryGlobal() with { FactoryHomeRoots = true });

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
        PreparedCase prepared = Prepared(OpenCodeAndFactoryGlobal());

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
        PreparedCase prepared = Prepared(OpenCodeAndFactoryGlobal());

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
        PreparedCase prepared = Prepared(new InstallSpec(
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
        PreparedCase prepared = Prepared(OpenCodeGlobal());

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
        PreparedCase prepared = Prepared(FactoryGlobal() with { MissingKyberDash = true });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Empty(prepared.Calls);
        Assert.Contains("capture plugin", prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skipped", prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A refused Factory install is a warning, not a failed deployment: the capture plugin is an
    ///     optional surface and the owner can rerun the printed command.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryKeepsTheSquadExitCodeWhenThePluginStepFails()
    {
        PreparedCase prepared = Prepared(FactoryGlobal() with { Executor = "exit-3" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("Successfully installed", prepared.Record.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("exit 3", prepared.Record.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The step relays the verb's own verdict line. The wording is kyberDash's, so the line carries a
    ///     neutral marker and this test never pins what KyberDash actually says.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryRelaysTheVerbsVerdictLine()
    {
        PreparedCase prepared = Prepared(FactoryGlobal() with { Executor = "verdict" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("FACTORY-VERDICT-LINE", prepared.Record.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A KyberDash that predates the capture plugin command is a named skip, not a warning: the
    ///     deployment must not report a failure it cannot fix.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryNamesAPreVerbKyberDashAsASkipAndNotAWarning()
    {
        PreparedCase prepared = Prepared(FactoryGlobal() with { Executor = "old-dash" });

        Assert.Equal(0, prepared.Record.ExitCode);
        Assert.Contains("capture plugin", prepared.Record.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "capture plugin step failed",
            prepared.Record.StandardOutput,
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

    /// <summary>One install input, which is also its recipe: every field that changes the run is in the key.</summary>
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
        bool FactoryHomeRoots = false)
    {
        public string Key => SquadInstallRecipe.Key(
            "install",
            Scope,
            SquadTargetCatalog.Parse(Targets),
            ("case", "squad-capture-plugin-step"),
            ("install", Recipe),
            ("version", Version),
            ("executor", Executor),
            ("no-capture-plugin", NoCapturePlugin ? "true" : null),
            ("dry-run", DryRun ? "true" : null),
            ("declined", Declined ? "true" : null),
            ("kyber-dash", MissingKyberDash ? "missing" : null),
            ("factory-home-roots", FactoryHomeRoots ? "true" : null));
    }

    private static InstallSpec OpenCodeGlobal() =>
        new("opencode-global", ["opencode"], SquadDeploymentScope.Global);

    private static InstallSpec FactoryGlobal() =>
        new("factory-global", ["factory"], SquadDeploymentScope.Global);

    private static InstallSpec OpenCodeAndFactoryGlobal() =>
        new("opencode-factory-global", ["opencode", "factory"], SquadDeploymentScope.Global);

    private static PreparedCase Prepared(InstallSpec spec) =>
        PreparedSquadInstalls.Get(spec.Key, () => Build(spec));

    /// <summary>
    /// Copies the prepared OpenCode global install into <paramref name="harness"/> and checks that the
    /// install it copies succeeded, so a passing update cannot come from a seed that wrote nothing.
    /// </summary>
    private static void CopyOpenCodeGlobal(Harness harness)
    {
        PreparedCase prepared = Prepared(OpenCodeGlobal());
        prepared.CopyInto(harness);
        Assert.Equal(0, prepared.Record.ExitCode);
    }

    /// <summary>
    /// Builds the one real install for <paramref name="spec"/> through the install command, with the
    /// plugin step's behaviour supplied by the executor its spec names.
    /// </summary>
    private static PreparedCase Build(InstallSpec spec)
    {
        TempDirectory? directory = new();
        try
        {
            string root = Path.Combine(directory.Path, "harness");
            string? factoryHome = spec.FactoryHomeRoots ? Path.Combine(root, "factory-home") : null;
            if (factoryHome is not null)
                Directory.CreateDirectory(factoryHome);

            using Harness h = new(root, factoryHome is null ? null : new SquadGlobalRoots(_ => null, factoryHome));
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

            PreparedCase prepared = new(
                spec.Key,
                directory,
                execution,
                executor.Calls,
                h.HomeDirectory,
                h.ApplicationDataDirectory,
                h.GlobalRootsDirectory,
                SquadTargetCatalog.Parse(spec.Targets).ToDictionary(target => target, target => h.GlobalRoot(target)),
                factoryHome);
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
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

    /// <summary>
    /// One prepared install: the command's record, the plugin-step calls it made, and the roots a test
    /// names when it checks those calls. The update tests copy it into their own harness.
    /// </summary>
    private sealed class PreparedCase(
        string key,
        TempDirectory directory,
        CommandExecution execution,
        IReadOnlyList<ProcessStartInfo> calls,
        string home,
        string applicationData,
        string globalRoots,
        IReadOnlyDictionary<SquadTarget, string> globalRootByTarget,
        string? factoryHome) : PreparedSquadInstall(
            key,
            directory,
            new SquadInstallRecord
            {
                ExitCode = execution.ExitCode,
                StandardOutput = execution.Output,
                StandardError = execution.ErrorOutput
            })
    {
        /// <summary>The plugin-step calls the command made, in order.</summary>
        public IReadOnlyList<ProcessStartInfo> Calls { get; } = calls;

        /// <summary>The home the install resolved its global roots and state under.</summary>
        public string Home { get; } = home;

        /// <summary>The home a Factory-only resolver resolved <c>~/.factory</c> against, or null.</summary>
        public string? FactoryHome { get; } = factoryHome;

        /// <summary>The prepared harness's global root for <paramref name="target"/>, as it resolved them.</summary>
        public string GlobalRoot(SquadTarget target) => globalRootByTarget[target];

        /// <summary>
        /// Copies the install into <paramref name="harness"/>'s own root as if it had run there: the
        /// deployed global roots, the home state and the application data, with the recorded home root
        /// re-rooted to the copy's and the deployed receipt digests recomputed for its one target.
        /// </summary>
        public void CopyInto(Harness harness)
        {
            ArgumentNullException.ThrowIfNull(harness);

            SquadInstallCopy.Tree(Home, harness.HomeDirectory);
            SquadInstallCopy.Tree(applicationData, harness.ApplicationDataDirectory);
            SquadInstallCopy.Tree(globalRoots, harness.GlobalRootsDirectory);

            string templateHome = SquadInstallCopy.Canonical(Home);
            string copyHome = SquadInstallCopy.Canonical(harness.HomeDirectory);
            SquadInstallCopy.ReRoot(
                Path.Combine(harness.HomeDirectory, ".kyber-weave", "squad"),
                templateHome,
                copyHome);
            _ = SquadInstallCopy.ReRootDeployed(
                harness.GlobalRoot(SquadTarget.OpenCode),
                harness.Store.ResolveReceiptPath(harness.HomeDirectory, SquadDeploymentScope.Global),
                harness.Store,
                templateHome,
                copyHome,
                SquadTargetCatalog.GetToken(SquadTarget.OpenCode));
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly ISquadGlobalRootResolver _globalRoots;
        private readonly SyntheticValidSquadReleaseSource _releaseSource = new();
        private readonly SquadStateStore _store;
        private readonly HomeUserPaths _userPaths;

        public Harness(string root, ISquadGlobalRootResolver? globalRoots = null)
        {
            Anchor = Path.Combine(root, "global-anchor");
            string applicationData = Path.Combine(root, "app-data");
            string home = Path.Combine(root, "home");
            GlobalRootsDirectory = Path.Combine(root, "global-roots");
            Directory.CreateDirectory(Anchor);
            Directory.CreateDirectory(applicationData);
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(GlobalRootsDirectory);
            _userPaths = new HomeUserPaths(applicationData, home);
            _store = new SquadStateStore(_userPaths);
            _globalRoots = globalRoots ?? new TempGlobalRoots(GlobalRootsDirectory);
        }

        public string Anchor { get; }

        public string HomeDirectory => _userPaths.HomeDirectory;

        public string ApplicationDataDirectory => _userPaths.ApplicationDataDirectory;

        public string GlobalRootsDirectory { get; }

        public SquadStateStore Store => _store;

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
            Directory.CreateDirectory(path);
            return path;
        }

        public SquadInstallCommand InstallCommand(
            IProcessExecutor executor,
            bool isInteractive = false,
            Func<string, bool>? readAnswer = null,
            Func<string?>? locateKyberDash = null)
        {
            return SquadTestCommands.Install(
                _userPaths,
                _store,
                _releaseSource,
                new ScriptedAgentRenderer("architect"),
                isInteractive,
                readAnswer ?? (_ => true),
                _globalRoots,
                PluginStep(executor, locateKyberDash));
        }

        public SquadUpdateCommand UpdateCommand(IProcessExecutor executor)
        {
            return SquadTestCommands.Update(
                _userPaths,
                _store,
                _releaseSource,
                new ScriptedAgentRenderer("architect"),
                false,
                _ => true,
                _globalRoots,
                null,
                null,
                PluginStep(executor));
        }

        private static KyberDashCapturePluginStep PluginStep(
            IProcessExecutor executor,
            Func<string?>? locateKyberDash = null)
        {
            return new KyberDashCapturePluginStep(executor, locateKyberDash ?? (() => FakeKyberDash));
        }
    }

    private sealed class HomeUserPaths(string applicationDataDirectory, string homeDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = applicationDataDirectory;

        public string HomeDirectory { get; } = homeDirectory;
    }

    private sealed class TempGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return Path.Combine(root, SquadTargetCatalog.GetToken(target));
        }
    }

    private sealed class ScriptedAgentRenderer(params string[] agentNames) : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = SquadTargetCatalog.All;

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            List<SquadDeploymentFile> files = [];
            foreach (SquadTarget target in request.Targets)
                foreach (string agent in agentNames)
                    files.Add(new SquadDeploymentFile(
                        $"agents/{agent}.md",
                        Encoding.UTF8.GetBytes($"canonical {agent}\n"),
                        SquadTargetCatalog.GetToken(target)));

            return Task.FromResult(new SquadRenderResult(
                true,
                files,
                [],
                [],
                []));
        }
    }
}
