using Xunit;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Packaging;
using Spectre.Console.Testing;

namespace KyberWeave.Tests;

/// <summary>What <c>squad pack</c> prints for each way a run can end, over a scripted packer and a test console.</summary>
public sealed class SquadPackCommandOutputTests
{
    [Fact]
    public void Execute_Packed_PrintsEachArchiveAndExitsZero()
    {
        (int exit, string output) = Run(new SquadPackOutcome(
            SquadPackStatus.Packed, null, "out/apm.tgz", "out/plugins.zip", "out/SHA256SUMS", ["careful"]));

        Assert.Equal(0, exit);
        Assert.Equal(
            "kyber-weave squad: warning: careful\n"
            + "Successfully packed APM archive to out/apm.tgz.\n"
            + "Successfully packed Agent Plugins archive to out/plugins.zip.\n"
            + "Generated checksums at out/SHA256SUMS.\n",
            output);
    }

    [Fact]
    public void Execute_InvalidFormat_PrintsTheValidFormatsAndExitsTwo()
    {
        (int exit, string output) = Run(new SquadPackOutcome(SquadPackStatus.InvalidFormat, null, null, null, null, []), "zip");

        Assert.Equal(2, exit);
        Assert.Equal("Invalid pack format 'zip'. Valid formats: apm, plugins, all.\n", output);
    }

    [Fact]
    public void Execute_NotRepositoryRoot_PointsAtInstallAndExitsOne()
    {
        (int exit, string output) = Run(new SquadPackOutcome(SquadPackStatus.NotRepositoryRoot, null, null, null, null, []));

        Assert.Equal(1, exit);
        Assert.Contains("kyber-weave squad pack is maintainer-only and must be run from the repository root.", output, StringComparison.Ordinal);
        Assert.Contains("run kyber-weave squad install instead.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_Failed_PrintsTheErrorAndExitsOne()
    {
        (int exit, string output) = Run(new SquadPackOutcome(SquadPackStatus.Failed, "bad [tree]", null, null, null, []));

        Assert.Equal(1, exit);
        Assert.Equal("kyber-weave squad: error: bad [tree]\n", output);
    }

    private static (int Exit, string Output) Run(SquadPackOutcome outcome, string format = "all")
    {
        TestConsole console = new() { EmitAnsiSequences = false };
        console.Profile.Width = 200;
        SquadPackCommand command = new(new ScriptedPacks(outcome), console);

        int exit = command.Execute(null!, new SquadPackSettings { Format = format });

        return (exit, console.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private sealed class ScriptedPacks(SquadPackOutcome outcome) : ISquadPackService
    {
        public SquadPackOutcome Pack(SquadPackRequest request) => outcome;
    }
}
