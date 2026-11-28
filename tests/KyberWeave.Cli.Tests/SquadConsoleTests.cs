using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What the Squad commands print and ask, over a test console and the shared console fake.</summary>
public sealed class SquadConsoleTests : IDisposable
{
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new();

    public void Dispose()
    {
        _ansi.Dispose();
        _streams.Dispose();
    }

    [Fact]
    public void WriteLine_LongLine_IsNotFolded()
    {
        _ansi.Profile.Width = 20;
        string line = new('x', 70);

        Console().WriteLine(line);

        Assert.Equal(line + "\n", Output());
        Assert.Equal(20, _ansi.Profile.Width);
    }

    [Fact]
    public void WriteClientInputError_EscapesTheMessage()
    {
        Console().WriteClientInputError("bad [path]");

        Assert.Equal("kyber-weave squad: error: bad [path]\n", Output());
    }

    [Fact]
    public void WriteTransactionWarnings_PrintsEachWarningAndNullPrintsNothing()
    {
        SquadConsole console = Console();

        console.WriteTransactionWarnings(null);
        console.WriteTransactionWarnings(["first", "second [x]"]);

        Assert.Equal("warning: first\nwarning: second [x]\n", Output());
    }

    [Fact]
    public void WriteMigrationNotes_OmitsRetainedLegacyRootsByDefault()
    {
        SquadMigrationReport report = new(["moved"], [new SquadUnmigratedLegacyRoot("/old", "why", "do this")], ["imported"], ["careful"]);

        Console().WriteMigrationNotes(report);

        Assert.Equal("info moved\ninfo imported\nwarning: careful\n", Output());
    }

    [Fact]
    public void WriteMigrationNotes_NamesRetainedLegacyRootsWhenAsked()
    {
        SquadMigrationReport report = new([], [new SquadUnmigratedLegacyRoot("/old", "why", "do this")], [], []);

        Console().WriteMigrationNotes(report, includeRetainedLegacyRoots: true);

        Assert.Equal("warning: legacy global root '/old' was not migrated: why do this\n", Output());
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("YES", true)]
    [InlineData("n", false)]
    [InlineData("", false)]
    public void ReadAnswer_OnlyYesProceeds(string typed, bool expected)
    {
        _streams.ScriptInput(typed + "\n");

        Assert.Equal(expected, Console().ReadAnswer("Go on?"));
        Assert.Equal("Go on? ", Output());
    }

    [Fact]
    public void ReadAnswer_EndOfInput_Declines()
    {
        Assert.False(Console().ReadAnswer("Go on?"));
    }

    [Fact]
    public void IsInteractive_FollowsRedirectedInput()
    {
        _streams.IsInputRedirected = true;

        Assert.False(Console().IsInteractive);
    }

    private SquadConsole Console() => new(_ansi, _streams);

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
}
