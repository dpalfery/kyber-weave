using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What the capture-plugin step prints for each way the KyberDash verb can end, on an injected console.</summary>
public sealed class KyberDashCapturePluginStepConsoleTests
{
    private readonly TestConsole _ansi = Wide();

    private static TestConsole Wide()
    {
        TestConsole console = new() { EmitAnsiSequences = false };
        console.Profile.Width = 400;
        return console;
    }

    [Fact]
    public void Run_KyberDashNotFound_SaysSkipped()
    {
        CapturePluginStepOutcome outcome = Step(new FakeProcessExecutor(), () => null).Run("/oc", false);

        Assert.Equal(CapturePluginStepOutcome.Skipped, outcome);
        Assert.StartsWith("skipped OpenCode capture plugin: KyberDash was not found", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_VerbSucceeds_PrintsItsFirstLineEscaped()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput("kyberdash", "\ninstalled [x]\nmore\n");

        CapturePluginStepOutcome outcome = Step(executor, () => "/fake/bin/kyberdash").Run("/oc", false);

        Assert.Equal(CapturePluginStepOutcome.Completed, outcome);
        Assert.Equal("installed [x]\n", Output());
    }

    [Fact]
    public void Run_VerbFails_WarnsWithTheRetryCommand()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput("kyberdash", "boom", exitCode: 3);

        CapturePluginStepOutcome outcome = Step(executor, () => "/fake/bin/kyberdash").Run("/oc", false);

        Assert.Equal(CapturePluginStepOutcome.Warned, outcome);
        Assert.Contains("OpenCode capture plugin step failed (exit 3): boom.", Output(), StringComparison.Ordinal);
        Assert.Contains("kyberdash kyber capture plugin install --harness opencode --preserve", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_OlderKyberDash_SaysItPredatesTheVerb()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput("kyberdash", "error: unknown command 'kyber'", exitCode: 1);

        CapturePluginStepOutcome outcome = Step(executor, () => "/fake/bin/kyberdash").Run("/oc", false);

        Assert.Equal(CapturePluginStepOutcome.Skipped, outcome);
        Assert.Contains("this KyberDash predates the capture plugin command", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_VerbCannotStart_WarnsAndLeavesTheDeploymentAlone()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithException("kyberdash", new InvalidOperationException("no exec"));

        CapturePluginStepOutcome outcome = Step(executor, () => "/fake/bin/kyberdash").Run("/oc", false);

        Assert.Equal(CapturePluginStepOutcome.Warned, outcome);
        Assert.Contains("OpenCode capture plugin step could not run KyberDash: no exec.", Output(), StringComparison.Ordinal);
    }

    private KyberDashCapturePluginStep Step(FakeProcessExecutor executor, Func<string?> locate) =>
        new(executor, locate, new SquadConsole(_ansi, new FakeConsole()));

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
}
