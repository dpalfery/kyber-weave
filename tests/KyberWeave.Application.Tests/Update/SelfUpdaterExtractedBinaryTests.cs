using KyberWeave.Cli.Update;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Update;

/// <summary>
/// The search for the binary inside an extracted archive: it finds exactly one candidate, never follows a link out of
/// the extraction directory, and refuses to guess when the archive holds more than one.
/// </summary>
public sealed class SelfUpdaterExtractedBinaryTests
{
    private const string Extract = "/kw/work/kyber-weave-extract";
    private const string FileName = "kyber-weave";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeProcessEnvironment _environment = new();

    [Fact]
    public void FindsTheSingleBinaryInANestedFolder()
    {
        _files.CreateDirectory(Extract + "/pkg");
        _files.WriteAllText(Extract + "/pkg/" + FileName, "x");

        Assert.Equal(Extract + "/pkg/" + FileName, Find());
    }

    [Fact]
    public void ReturnsTheBinarySittingAtTheTopLevel()
    {
        _files.CreateDirectory(Extract);
        _files.WriteAllText(Extract + "/" + FileName, "x");

        Assert.Equal(Extract + "/" + FileName, Find());
    }

    [Fact]
    public void RefusesToGuessWhenMoreThanOneCandidateMatches()
    {
        _files.CreateDirectory(Extract + "/one");
        _files.CreateDirectory(Extract + "/two");
        _files.WriteAllText(Extract + "/one/" + FileName, "x");
        _files.WriteAllText(Extract + "/two/" + FileName, "x");

        Assert.Null(Find());
    }

    [Fact]
    public void DoesNotFollowADirectoryLinkOutOfTheExtractionDirectory()
    {
        _files.CreateDirectory("/kw/outside");
        _files.WriteAllText("/kw/outside/" + FileName, "x");
        _files.CreateDirectory(Extract);
        _files.CreateDirectoryLink(Extract + "/escape", "/kw/outside");

        Assert.Null(Find());
    }

    [Fact]
    public void DoesNotReturnAFileThatIsItselfALink()
    {
        _files.CreateDirectory("/kw/outside");
        _files.WriteAllText("/kw/outside/" + FileName, "x");
        _files.CreateDirectory(Extract);
        _files.CreateFileLink(Extract + "/" + FileName, "/kw/outside/" + FileName);

        Assert.Null(Find());
    }

    /// <summary>
    /// The match follows the volume's name rule, not the operating system's name: macOS usually has a case-insensitive
    /// volume, and Linux never does. Keying the walk to "Windows" would miss a good archive on the first.
    /// </summary>
    [Fact]
    public void TheVolumesNameRuleDecidesWhetherACaseDifferenceStillMatches()
    {
        _files.CreateDirectory(Extract + "/pkg");
        _files.WriteAllText(Extract + "/pkg/" + FileName.ToUpperInvariant(), "x");
        FakeProcessEnvironment insensitive = new(isFileSystemCaseInsensitive: true);

        Assert.Equal(
            Extract + "/pkg/" + FileName.ToUpperInvariant(),
            SelfUpdater.TryFindExtractedBinary(_files, insensitive, Extract, FileName));
        Assert.Null(Find());
    }

    private string? Find() => SelfUpdater.TryFindExtractedBinary(_files, _environment, Extract, FileName);
}
