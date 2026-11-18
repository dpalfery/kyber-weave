using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What <c>squad run</c> prints when it refuses its input, over a test console.</summary>
public sealed class SquadRunCommandOutputTests
{
    [Theory]
    [InlineData("", "kyber-weave squad: error: No target harness specified. Specify a harness target (e.g. claude, codex).\n")]
    [InlineData("  ", "kyber-weave squad: error: No target harness specified. Specify a harness target (e.g. claude, codex).\n")]
    public void Execute_NoHarness_PrintsTheClientErrorAndExitsTwo(string harness, string expected)
    {
        TestConsole ansi = new() { EmitAnsiSequences = false };
        SquadRunCommand command = SquadTestCommands.Run(output: new SquadConsole(ansi, new FakeConsole()));

        int exit = command.Execute(null!, new SquadRunSettings { Harness = harness });

        Assert.Equal(2, exit);
        Assert.Equal(expected, ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
