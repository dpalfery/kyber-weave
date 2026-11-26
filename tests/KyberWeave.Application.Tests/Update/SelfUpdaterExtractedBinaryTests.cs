using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using KyberWeave.Application.Update;

namespace KyberWeave.Application.Tests.Update;

/// <summary>
/// The search for the binary inside an extracted archive: it finds exactly one candidate, never follows a link out of
/// the extraction directory, and refuses to guess when the archive holds more than one.
/// </summary>
public sealed class SelfUpdaterExtractedBinaryTests
{
    private const string FileName = "kyber-weave";

    private static readonly string Root = new InMemoryFileSystem().Root;
    private static readonly string Outside = Path.Combine(Root, "kw", "outside");
    private static readonly string Extract = Path.Combine(Root, "kw", "work", "kyber-weave-extract");

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeProcessEnvironment _environment = new();

    [Fact]
    public void FindsTheSingleBinaryInANestedFolder()
    {
        _files.CreateDirectory(Path.Combine(Extract, "pkg"));
        _files.WriteAllText(Path.Combine(Extract, "pkg", FileName), "x");

        Assert.Equal(Path.Combine(Extract, "pkg", FileName), Find());
    }

    [Fact]
    public void ReturnsTheBinarySittingAtTheTopLevel()
    {
        _files.CreateDirectory(Extract);
        _files.WriteAllText(Path.Combine(Extract, FileName), "x");

        Assert.Equal(Path.Combine(Extract, FileName), Find());
    }

    [Fact]
    public void RefusesToGuessWhenMoreThanOneCandidateMatches()
    {
        _files.CreateDirectory(Path.Combine(Extract, "one"));
        _files.CreateDirectory(Path.Combine(Extract, "two"));
        _files.WriteAllText(Path.Combine(Extract, "one", FileName), "x");
        _files.WriteAllText(Path.Combine(Extract, "two", FileName), "x");

        Assert.Null(Find());
    }

    [Fact]
    public void DoesNotFollowADirectoryLinkOutOfTheExtractionDirectory()
    {
        _files.CreateDirectory(Outside);
        _files.WriteAllText(Path.Combine(Outside, FileName), "x");
        _files.CreateDirectory(Extract);
        _files.CreateDirectoryLink(Path.Combine(Extract, "escape"), Outside);

        Assert.Null(Find());
    }

    [Fact]
    public void DoesNotReturnAFileThatIsItselfALink()
    {
        _files.CreateDirectory(Outside);
        _files.WriteAllText(Path.Combine(Outside, FileName), "x");
        _files.CreateDirectory(Extract);
        _files.CreateFileLink(Path.Combine(Extract, FileName), Path.Combine(Outside, FileName));

        Assert.Null(Find());
    }

    /// <summary>
    /// The match follows the volume's name rule, not the operating system's name: macOS usually has a case-insensitive
    /// volume, and Linux never does. Keying the walk to "Windows" would miss a good archive on the first.
    /// </summary>
    [Fact]
    public void TheVolumesNameRuleDecidesWhetherACaseDifferenceStillMatches()
    {
        _files.CreateDirectory(Path.Combine(Extract, "pkg"));
        _files.WriteAllText(Path.Combine(Extract, "pkg", FileName.ToUpperInvariant()), "x");
        FakeProcessEnvironment insensitive = new(isFileSystemCaseInsensitive: true);

        Assert.Equal(
            Path.Combine(Extract, "pkg", FileName.ToUpperInvariant()),
            SelfUpdater.TryFindExtractedBinary(_files, insensitive, Extract, FileName));
        Assert.Null(Find());
    }

    private string? Find() => SelfUpdater.TryFindExtractedBinary(_files, _environment, Extract, FileName);
}
