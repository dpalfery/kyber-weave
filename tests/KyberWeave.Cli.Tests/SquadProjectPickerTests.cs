using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the pure seams behind the global project picker and the <c>--projects</c> parser: the labels the
/// operator is offered, the rule that refuses <c>None</c> beside another choice, the mapping from a
/// selection to project paths, and the keyword parse. The Spectre prompt itself is a thin shell over these.
/// </summary>
public sealed class SquadProjectPickerTests
{
    private static readonly DateTimeOffset Updated = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static SquadProjectRegistryEntry Live(string path) =>
        new($"key-{path}", path, Updated, ["codex"], Stale: false, StaleReason: null);

    private static SquadProjectRegistryEntry Stale(string path, string reason) =>
        new($"key-{path}", path, Updated, ["codex"], Stale: true, StaleReason: reason);

    [Fact]
    public void BuildOffersAllThenNoneThenEachProjectPath()
    {
        IReadOnlyList<string> labels = SquadProjectPickerChoices.Build([Live("/work/alpha"), Live("/work/beta")]);

        Assert.Equal(["All", "None", "/work/alpha", "/work/beta"], labels);
    }

    [Fact]
    public void BuildLabelsAStaleProjectWithItsReason()
    {
        IReadOnlyList<string> labels = SquadProjectPickerChoices.Build([
            Live("/work/alpha"),
            Stale("/work/gone", "registered project path no longer exists")
        ]);

        Assert.Equal("/work/gone (stale: registered project path no longer exists)", labels[3]);
    }

    [Fact]
    public void BuildWithNoProjectsOffersOnlyAllAndNone()
    {
        IReadOnlyList<string> labels = SquadProjectPickerChoices.Build([]);

        Assert.Equal(["All", "None"], labels);
    }

    [Fact]
    public void AnEmptySelectionIsValidAndMeansNothing()
    {
        Assert.True(SquadProjectPickerChoices.TryValidateProjectChoices([], out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("All")]
    [InlineData("None")]
    [InlineData("All|/work/alpha")]
    [InlineData("/work/alpha|/work/beta")]
    public void ValidSelectionsAreAccepted(string encodedChoices)
    {
        bool valid = SquadProjectPickerChoices.TryValidateProjectChoices(Split(encodedChoices), out string? error);

        Assert.True(valid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("None|All")]
    [InlineData("None|/work/alpha")]
    [InlineData("/work/alpha|None")]
    public void NoneCombinedWithAnotherChoiceIsRefusedWithAReason(string encodedChoices)
    {
        bool valid = SquadProjectPickerChoices.TryValidateProjectChoices(Split(encodedChoices), out string? error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("None", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AllSelectsEveryNonStaleProjectAndNothingElse()
    {
        IReadOnlyList<SquadProjectRegistryEntry> projects =
        [
            Live("/work/alpha"),
            Stale("/work/gone", "gone"),
            Live("/work/beta")
        ];

        IReadOnlyList<string> paths = SquadProjectPickerChoices.ToPaths(["All"], projects);

        Assert.Equal(["/work/alpha", "/work/beta"], paths);
    }

    [Fact]
    public void NoneSelectsNothing()
    {
        IReadOnlyList<SquadProjectRegistryEntry> projects = [Live("/work/alpha"), Live("/work/beta")];

        Assert.Empty(SquadProjectPickerChoices.ToPaths(["None"], projects));
        Assert.Empty(SquadProjectPickerChoices.ToPaths([], projects));
    }

    [Fact]
    public void ChosenProjectsMapToTheirPathsInRegistryOrderWithoutDuplicates()
    {
        IReadOnlyList<SquadProjectRegistryEntry> projects = [Live("/work/alpha"), Live("/work/beta")];

        IReadOnlyList<string> paths = SquadProjectPickerChoices.ToPaths(
            ["/work/beta", "/work/alpha", "/work/beta"],
            projects);

        Assert.Equal(["/work/alpha", "/work/beta"], paths);
    }

    [Fact]
    public void AStaleProjectChosenByNameIsKeptSoTheCallerCanWarnAboutIt()
    {
        IReadOnlyList<SquadProjectRegistryEntry> projects = [Live("/work/alpha"), Stale("/work/gone", "gone")];
        string staleLabel = SquadProjectPickerChoices.Build(projects)[3];

        IReadOnlyList<string> paths = SquadProjectPickerChoices.ToPaths([staleLabel], projects);

        Assert.Equal(["/work/gone"], paths);
    }

    [Fact]
    public void ParseAbsentWhenNoValueIsGiven()
    {
        SquadProjectSelection selection = SquadCommandComposition.ParseProjectSelection(null);

        Assert.Equal(SquadProjectSelectionKind.Absent, selection.Kind);
    }

    [Theory]
    [InlineData("all", "All")]
    [InlineData("ALL", "All")]
    [InlineData("none", "None")]
    public void ParseKeywordsAloneSelectTheirChoice(string keyword, string expectedKind)
    {
        SquadProjectSelection selection = SquadCommandComposition.ParseProjectSelection([keyword]);

        Assert.Equal(expectedKind, selection.Kind.ToString());
    }

    [Fact]
    public void ParsePathsKeepsEachDistinctPath()
    {
        SquadProjectSelection selection = SquadCommandComposition.ParseProjectSelection(
            ["/work/alpha", "/work/beta", "/work/alpha"]);

        Assert.Equal(SquadProjectSelectionKind.Paths, selection.Kind);
        Assert.Equal(["/work/alpha", "/work/beta"], selection.Paths);
    }

    [Theory]
    [InlineData("none|all")]
    [InlineData("none|/work/alpha")]
    [InlineData("all|/work/alpha")]
    public void ParseRefusesKeywordsCombinedWithAnotherChoice(string encodedValues)
    {
        Assert.Throws<ArgumentException>(() => SquadCommandComposition.ParseProjectSelection(Split(encodedValues)));
    }

    [Fact]
    public void ParseRefusesAnEmptyValue()
    {
        Assert.Throws<ArgumentException>(() => SquadCommandComposition.ParseProjectSelection(["  "]));
    }

    private static string[] Split(string encoded) => encoded.Split('|');
}
