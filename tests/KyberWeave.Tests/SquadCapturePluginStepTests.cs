using System.Diagnostics;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

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

    [Fact]
    public void GlobalInstallWithOpenCodeRunsThePluginStepOnceAfterSuccess()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-opencode"));
        FakeProcessExecutor executor = new();

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["opencode"])), out _);

        Assert.Equal(0, execution.ExitCode);
        ProcessStartInfo call = Assert.Single(executor.Calls);
        Assert.Equal(
            [
                "kyber", "capture", "plugin", "install", "--harness", "opencode", "--preserve", "--config-dir",
                h.GlobalRoot(SquadTarget.OpenCode)
            ],
            call.ArgumentList);
    }

    [Fact]
    public void GlobalInstallWithoutOpenCodeDoesNotRunThePluginStep()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-codex"));
        FakeProcessExecutor executor = new();

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["codex"])), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void GlobalInstallWithNoCapturePluginDoesNotRunThePluginStep()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-no-plugin"));
        FakeProcessExecutor executor = new();
        SquadInstallSettings settings = GlobalInstall(h, ["opencode"]);
        settings.NoCapturePlugin = true;

        CommandExecution execution = Capture(() => h.InstallCommand(executor).Execute(null!, settings), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void GlobalInstallDryRunPassesDryRunThroughToThePluginStep()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-dry-run"));
        FakeProcessExecutor executor = new();
        SquadInstallSettings settings = GlobalInstall(h, ["opencode"]);
        settings.DryRun = true;

        CommandExecution execution = Capture(() => h.InstallCommand(executor).Execute(null!, settings), out _);

        Assert.Equal(0, execution.ExitCode);
        ProcessStartInfo call = Assert.Single(executor.Calls);
        Assert.Equal("--dry-run", call.ArgumentList[^1]);
    }

    [Fact]
    public void ProjectInstallWithOpenCodeDoesNotRunThePluginStep()
    {
        // The plugin is global capture, so a project-scope install never touches the OpenCode global root.
        using Harness h = new(Path.Combine(_temp.Path, "install-project"));
        FakeProcessExecutor executor = new();
        string project = h.MakeDirectory("project");

        CommandExecution execution = Capture(() => h.InstallCommand(executor).Execute(null!, new SquadInstallSettings
        {
            Path = project,
            Global = false,
            Targets = ["opencode"],
            Version = InstalledVersion,
            Yes = true
        }), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void DeclinedGlobalInstallDoesNotRunThePluginStep()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-declined"));
        FakeProcessExecutor executor = new();
        SquadInstallSettings settings = GlobalInstall(h, ["opencode"]);
        settings.Yes = false;

        CommandExecution execution = Capture(
            () => h.InstallCommand(executor, true, _ => false).Execute(null!, settings),
            out _);

        Assert.Equal(2, execution.ExitCode);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void PluginStepFailureLeavesTheSquadExitCodeUnchanged()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-plugin-fails"));
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput("kyberdash", string.Empty, 3);

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["opencode"])), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Successfully installed", execution.Output, StringComparison.Ordinal);
        Assert.Contains("warning", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginStepThatCannotBeExecutedLeavesTheSquadExitCodeUnchanged()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-plugin-not-executable"));
        FakeProcessExecutor executor = new FakeProcessExecutor().WithException(
            "kyberdash",
            new UnauthorizedAccessException("Access to the path '/fake/bin/kyberdash' is denied."));

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["opencode"])), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Successfully installed", execution.Output, StringComparison.Ordinal);
        Assert.Contains("warning", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUpdateWithOpenCodeRunsThePluginStepAfterTheDeploymentUpdates()
    {
        using Harness h = new(Path.Combine(_temp.Path, "update-opencode"));
        Assert.Equal(0, Capture(() => h.InstallCommand(new FakeProcessExecutor())
            .Execute(null!, GlobalInstall(h, ["opencode"])), out _).ExitCode);
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
        Assert.Equal(0, Capture(() => h.InstallCommand(new FakeProcessExecutor())
            .Execute(null!, GlobalInstall(h, ["opencode"])), out _).ExitCode);
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
        using Harness h = new(Path.Combine(_temp.Path, "install-guidance"));
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            $"opencode plugin: installed /fake/opencode/plugins/kyberdash-opencode.js\n{HarnessPrefix}{GuidanceAlpha}\n");

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["opencode"])), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(GuidanceAlpha, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUpdateRelaysTheVerbGuidanceAndKeepsTheSquadExitCode()
    {
        using Harness h = new(Path.Combine(_temp.Path, "update-guidance"));
        Assert.Equal(0, Capture(() => h.InstallCommand(new FakeProcessExecutor())
            .Execute(null!, GlobalInstall(h, ["opencode"])), out _).ExitCode);
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
        using Harness h = new(Path.Combine(_temp.Path, "install-factory"));
        FakeProcessExecutor executor = new();

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["factory"])), out _);

        Assert.Equal(0, execution.ExitCode);
        ProcessStartInfo call = Assert.Single(executor.Calls);
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
        string home = Path.Combine(_temp.Path, "factory-home");
        Directory.CreateDirectory(home);
        using Harness h = new(
            Path.Combine(_temp.Path, "install-factory-root"),
            new SquadGlobalRoots(_ => null, home));
        FakeProcessExecutor executor = new();

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["factory"])), out _);

        Assert.Equal(0, execution.ExitCode);
        // The repository resolves the Factory tree to ~/.factory: the tree the verb resolves for itself.
        Assert.Equal(Path.Combine(home, ".factory"), h.GlobalRoot(SquadTarget.Factory));
        ProcessStartInfo call = Assert.Single(executor.Calls);
        Assert.Equal(string.Empty, ConfigDirOf(call));
        Assert.DoesNotContain("--config-dir", call.ArgumentList);
        Assert.DoesNotContain(h.GlobalRoot(SquadTarget.Factory), call.ArgumentList);
    }

    /// <summary>
    ///     The two steps never share a root: the OpenCode plugin lives under the OpenCode global root
    ///     and keeps its <c>--config-dir</c>, while the Factory step names no root at all, so it can
    ///     never be handed the OpenCode one.
    /// </summary>
    [Fact]
    public void GlobalInstallWithOpenCodeAndFactoryGivesOnlyTheOpenCodeStepARoot()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-opencode-factory-roots"));
        FakeProcessExecutor executor = new();

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["opencode", "factory"])), out _);

        Assert.Equal(0, execution.ExitCode);
        IReadOnlyList<ProcessStartInfo> calls = executor.Calls;
        Assert.Equal(2, calls.Count);
        // The OpenCode step keeps its own root: that flag still selects the OpenCode config root.
        Assert.Equal(h.GlobalRoot(SquadTarget.OpenCode), ConfigDirOf(calls[0]));
        // The Factory step names no root, so it cannot be handed the OpenCode one.
        Assert.Equal(string.Empty, ConfigDirOf(calls[1]));
        Assert.DoesNotContain(h.GlobalRoot(SquadTarget.OpenCode), calls[1].ArgumentList);
        Assert.NotEqual(h.GlobalRoot(SquadTarget.OpenCode), h.GlobalRoot(SquadTarget.Factory));
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
        using Harness h = new(Path.Combine(_temp.Path, "install-opencode-factory-order"));
        FakeProcessExecutor executor = new();

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["opencode", "factory"])), out _);

        Assert.Equal(0, execution.ExitCode);
        IReadOnlyList<ProcessStartInfo> calls = executor.Calls;
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
        using Harness h = new(Path.Combine(_temp.Path, "install-project-factory"));
        FakeProcessExecutor executor = new();
        string project = h.MakeDirectory("project-factory");

        CommandExecution execution = Capture(() => h.InstallCommand(executor).Execute(null!, new SquadInstallSettings
        {
            Path = project,
            Global = false,
            Targets = ["factory"],
            Version = InstalledVersion,
            Yes = true
        }), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Empty(executor.Calls);
    }

    /// <summary>
    ///     A global deployment that does not include Factory runs the OpenCode plugin step and nothing
    ///     else: the Factory plugin is installed only for a Factory target.
    /// </summary>
    [Fact]
    public void GlobalInstallWithoutFactoryRunsOnlyTheOpenCodePluginStep()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-opencode-only"));
        FakeProcessExecutor executor = new();

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["opencode"])), out _);

        Assert.Equal(0, execution.ExitCode);
        ProcessStartInfo call = Assert.Single(executor.Calls);
        Assert.Equal("opencode", HarnessOf(call));
    }

    /// <summary>
    ///     A missing KyberDash is a skip, not a failure: the deployment that already succeeded keeps its
    ///     exit code and nothing is started.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryAndNoKyberDashSkipsThePluginStepAndKeepsTheExitCode()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-factory-no-dash"));
        FakeProcessExecutor executor = new();

        CommandExecution execution = Capture(
            () => h.InstallCommand(executor, locateKyberDash: () => null)
                .Execute(null!, GlobalInstall(h, ["factory"])),
            out string output);

        Assert.Equal(0, execution.ExitCode);
        Assert.Empty(executor.Calls);
        Assert.Contains("capture plugin", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skipped", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A refused Factory install is a warning, not a failed deployment: the capture plugin is an
    ///     optional surface and the owner can rerun the printed command.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryKeepsTheSquadExitCodeWhenThePluginStepFails()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-factory-plugin-fails"));
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput("kyberdash", string.Empty, 3);

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["factory"])), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Successfully installed", execution.Output, StringComparison.Ordinal);
        Assert.Contains("exit 3", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The step relays the verb's own verdict line. The wording is kyberDash's, so the line carries a
    ///     neutral marker and this test never pins what KyberDash actually says.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryRelaysTheVerbsVerdictLine()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-factory-verdict"));
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            "FACTORY-VERDICT-LINE\n");

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["factory"])), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("FACTORY-VERDICT-LINE", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A KyberDash that predates the capture plugin command is a named skip, not a warning: the
    ///     deployment must not report a failure it cannot fix.
    /// </summary>
    [Fact]
    public void GlobalInstallWithFactoryNamesAPreVerbKyberDashAsASkipAndNotAWarning()
    {
        using Harness h = new(Path.Combine(_temp.Path, "install-factory-old-dash"));
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput(
            "kyberdash",
            "error: unknown command 'capture'\n",
            1);

        CommandExecution execution =
            Capture(() => h.InstallCommand(executor).Execute(null!, GlobalInstall(h, ["factory"])), out _);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("capture plugin", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("capture plugin step failed", execution.Output, StringComparison.OrdinalIgnoreCase);
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

    private static CommandExecution Capture(Func<int> execute, out string output)
    {
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(execute);
        output = captured.Output;
        return new CommandExecution(captured.Result, captured.Output);
    }

    private static CapturePluginStepOutcome Capture(Func<CapturePluginStepOutcome> step, out string output)
    {
        CapturedConsoleExecution<CapturePluginStepOutcome> captured = ProcessConsoleCapture.Run(step);
        output = captured.Output;
        return captured.Result;
    }

    private static SquadInstallSettings GlobalInstall(Harness h, string[] targets)
    {
        return new SquadInstallSettings
        {
            Path = h.Anchor,
            Global = true,
            Targets = targets,
            Version = InstalledVersion,
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

    private sealed record CommandExecution(int ExitCode, string Output);

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
            string globalRootsDirectory = Path.Combine(root, "global-roots");
            Directory.CreateDirectory(Anchor);
            Directory.CreateDirectory(applicationData);
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(globalRootsDirectory);
            _userPaths = new HomeUserPaths(applicationData, home);
            _store = new SquadStateStore(_userPaths);
            _globalRoots = globalRoots ?? new TempGlobalRoots(globalRootsDirectory);
        }

        public string Anchor { get; }

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
            return new SquadInstallCommand(
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
            return new SquadUpdateCommand(
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
