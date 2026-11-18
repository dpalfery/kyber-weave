using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What the project picker asks and returns when a person answers it on an injected console.</summary>
public sealed class SpectreSquadProjectPickerTests
{
    private static readonly SquadProjectRegistryEntry[] Projects =
    [
        new("a", "/p/a", DateTimeOffset.UnixEpoch, ["codex"], false, null),
        new("b", "/p/b", DateTimeOffset.UnixEpoch, ["codex"], true, "gone")
    ];

    private readonly TestConsole _console = Interactive();

    private static TestConsole Interactive()
    {
        TestConsole console = new() { EmitAnsiSequences = false };
        console.Profile.Capabilities.Interactive = true;
        return console;
    }

    [Fact]
    public void Pick_NoProjects_AsksNothing()
    {
        IReadOnlyList<string> picked = new SpectreSquadProjectPicker(_console).Pick([]);

        Assert.Empty(picked);
        Assert.Equal(string.Empty, _console.Output);
    }

    [Fact]
    public void Pick_All_ReturnsTheProjectsThatAreNotStale()
    {
        _console.Input.PushKey(ConsoleKey.Spacebar);
        _console.Input.PushKey(ConsoleKey.Enter);

        IReadOnlyList<string> picked = new SpectreSquadProjectPicker(_console).Pick(Projects);

        Assert.Equal(["/p/a"], picked);
        Assert.Contains("Also update these registered projects?", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Pick_NoneBesideAnotherChoice_ReportsItAndAsksAgain()
    {
        foreach (ConsoleKey key in new[]
                 {
                     ConsoleKey.Spacebar, ConsoleKey.DownArrow, ConsoleKey.Spacebar, ConsoleKey.Enter,
                     ConsoleKey.DownArrow, ConsoleKey.Spacebar, ConsoleKey.Enter
                 })
            _console.Input.PushKey(key);

        IReadOnlyList<string> picked = new SpectreSquadProjectPicker(_console).Pick(Projects);

        Assert.Empty(picked);
        Assert.Contains("None selects no project, so it cannot be combined", _console.Output, StringComparison.Ordinal);
    }
}
