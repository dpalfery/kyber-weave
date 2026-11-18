using KyberWeave.Testing.Squad;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// What the in-memory crash-safety snapshots leave out. A kill before the journal exists can leave the empty state
/// folders its run had made, and no later run can know they were its own, so those folders are left out; anything
/// else under the home is part of what the sweep compares.
/// </summary>
public sealed class InMemorySquadCrashSafetyFixtureTests
{
    [Fact]
    public void EveryFolderLeftOutOfASnapshotIsOnTheStateFolderChain()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Install);
        Assert.False(fixture.Files.DirectoryExists(StateChain(fixture, "home/notes")));

        fixture.Files.CreateDirectory(StateChain(fixture, "home/notes"));

        Assert.Contains("home/notes", fixture.Capture().Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void AnEmptyStateFolderAHandledFailureLeavesIsNotASnapshotDifference()
    {
        using InMemorySquadCrashSafetyFixture fixture = new();
        fixture.CreateBaseline(SquadCrashFlow.Install);
        string projects = StateChain(fixture, "home/.kyber-weave/squad/projects");
        IReadOnlyDictionary<string, SquadTreeEntry> before = fixture.Capture();

        fixture.Files.CreateDirectory(projects);

        Assert.Null(SquadTreeComparison.Difference(before, fixture.Capture()));
    }

    private static string StateChain(InMemorySquadCrashSafetyFixture fixture, string relativePath) =>
        Path.Combine(
            fixture.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
}
