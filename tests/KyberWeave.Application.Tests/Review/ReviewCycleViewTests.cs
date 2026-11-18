using KyberWeave.Application.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The cycle view decides which cycle folder a caller's path belongs to and whether one path lies under a folder.
/// Both answers used to read the process working directory; these tests prove they follow the injected seams instead.
/// </summary>
public sealed class ReviewCycleViewTests
{
    private readonly InMemoryFileSystem _files = new();

    private readonly FakePathResolver _paths;

    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/work");

    public ReviewCycleViewTests() => _paths = new FakePathResolver(_files);

    [Fact]
    public void AReasonablySpelledCycleReportIsPlacedInTheCycleFolderUnderTheInjectedWorkingDirectory()
    {
        bool found = ReviewCycleView.TryFromOutPath(
            _paths, _environment, "task/review/cycle-2/gates.json", out string reviewFolder, out int cycle);

        Assert.True(found);
        Assert.Equal("/work/task/review", reviewFolder);
        Assert.Equal(2, cycle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0spelling/review/cycle-1/gates.json")]
    public void AnUnusableReportPathNamesNoCycleAndDoesNotThrow(string outPath)
    {
        Assert.False(ReviewCycleView.TryFromOutPath(
            _paths, _environment, outPath, out string reviewFolder, out int cycle));
        Assert.Equal(string.Empty, reviewFolder);
        Assert.Equal(0, cycle);
    }

    [Fact]
    public void AFullCycleReportIsPlacedByItsOwnPath()
    {
        bool found = ReviewCycleView.TryFromOutPath(
            _paths, _environment, "/repo/task/review/cycle-7/findings.json", out string reviewFolder, out int cycle);

        Assert.True(found);
        Assert.Equal("/repo/task/review", reviewFolder);
        Assert.Equal(7, cycle);
    }

    [Fact]
    public void APathOutsideACycleFolderNamesNoCycle()
    {
        Assert.False(ReviewCycleView.TryFromOutPath(
            _paths, _environment, "task/review/findings.json", out string reviewFolder, out int cycle));
        Assert.Equal(string.Empty, reviewFolder);
        Assert.Equal(0, cycle);
    }

    [Fact]
    public void ACycleFolderUnderAFolderNotNamedReviewNamesNoCycle()
    {
        Assert.False(ReviewCycleView.TryFromOutPath(
            _paths, _environment, "/repo/task/cycles/cycle-2/gates.json", out _, out _));
    }

    [Fact]
    public void AReportInsideTheCycleFolderIsUnderIt()
    {
        Assert.True(ReviewCycleView.IsUnder(
            _paths, _environment, "/repo/task/review/cycle-2", "/repo/task/review/cycle-2/gates.json"));
    }

    [Fact]
    public void ARelativelySpelledReportIsJudgedAgainstTheInjectedWorkingDirectory()
    {
        Assert.True(ReviewCycleView.IsUnder(_paths, _environment, "/work/review", "review/cycle-1/gates.json"));
        Assert.False(ReviewCycleView.IsUnder(_paths, _environment, "/work/review", "review-other/gates.json"));
    }

    [Fact]
    public void ASiblingWhoseNameStartsWithTheFolderNameIsNotUnderIt()
    {
        Assert.False(ReviewCycleView.IsUnder(
            _paths, _environment, "/repo/review", "/repo/review-backup/gates.json"));
    }

    [Fact]
    public void AFolderIsUnderItself()
    {
        Assert.True(ReviewCycleView.IsUnder(_paths, _environment, "/repo/review", "/repo/review"));
    }

    [Fact]
    public void AChildSpelledInAnotherCaseIsUnderItsFolderOnACaseInsensitiveVolume()
    {
        FakePathResolver paths = FakePathResolver.OverEmptyFileSystem(caseSensitive: false);

        Assert.True(ReviewCycleView.IsUnder(
            paths, _environment, "/repo/Review", "/repo/review/cycle-1/gates.json"));
    }

    [Fact]
    public void ALinkedFolderThatPointsOutsideTheTreeIsNotUnderIt()
    {
        _files.CreateDirectory("/repo/tree");
        _files.CreateDirectory("/tmp/evil/review");
        _files.CreateDirectoryLink("/repo/tree/review", "/tmp/evil/review");

        Assert.False(ReviewCycleView.IsUnder(
            _paths, _environment, "/repo/tree", "/repo/tree/review/gates.json"));
    }
}
