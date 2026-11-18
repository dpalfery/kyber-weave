using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Pins <see cref="CorpusLines.Split"/> to <see cref="File.ReadAllLines(string)"/>'s answers, because three rule
/// suites read shipped YAML through it and a wrong split silently changes what they count.
/// </summary>
public sealed class CorpusLinesTests
{
    [Fact]
    public void AnEmptyFileHasNoLines()
    {
        Assert.Empty(CorpusLines.Split(string.Empty));
    }

    [Fact]
    public void ALineBreakOnlyFileHasOneEmptyLine()
    {
        Assert.Equal([string.Empty], CorpusLines.Split("\n"));
    }

    [Fact]
    public void AFinalLineBreakTerminatesTheLastLineRatherThanStartingAnother()
    {
        Assert.Equal(["a", "b"], CorpusLines.Split("a\nb\n"));
    }

    [Fact]
    public void CarriageReturnsAreNotPartOfTheLine()
    {
        Assert.Equal(["a", "b"], CorpusLines.Split("a\r\nb"));
    }

    [Fact]
    public void ATrailingBlankLineIsAnEmptyEntry()
    {
        Assert.Equal(["a", string.Empty], CorpusLines.Split("a\n\n"));
    }
}
