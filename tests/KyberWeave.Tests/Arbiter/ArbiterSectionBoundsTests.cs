using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Sections;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Characterization (task 3.21, should pass now): the bounds on the section split and the section selector. A document
/// is read in bounded pieces, and a bound that is hit is reported as truncation or as fail-safe, never as silent loss.
/// </summary>
public sealed class ArbiterSectionBoundsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A provider that is never asked and answers nothing; the selector must fail safe on the size cap before any call.</summary>
    private sealed class UnaskedProvider : IArbiterProvider
    {
        public int CallCount { get; private set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ArbiterStep1BatchResult(
                new Dictionary<string, string>(StringComparer.Ordinal), null, null, false));
        }
    }

    /// <summary>A heading past the scan bound is not a section: the split stops at the bound and marks its last section truncated.</summary>
    [Fact]
    public void AHeadingPastTheScanBoundIsNotASectionAndTheLastSectionIsMarkedTruncated()
    {
        int lateLine = DocumentSectionIndex.MaxScannedLines + 5;
        List<string> lines = ["# Top", "Opening text."];
        while (lines.Count < lateLine - 1)
        {
            lines.Add("filler line");
        }

        lines.Add("## Late heading");
        lines.Add("Text after the bound.");
        IReadOnlyList<DocumentSection> sections = DocumentSectionIndex.Split(string.Join("\n", lines));

        Assert.DoesNotContain(sections, section => section.Title.Contains("Late", StringComparison.Ordinal));
        Assert.True(sections[^1].Truncated, "the section cut by the scan bound must say it was truncated");
        Assert.True(sections[^1].EndLine <= DocumentSectionIndex.MaxScannedLines);
    }

    /// <summary>A section longer than the per-section cap is cut to the cap and marked truncated, never returned whole.</summary>
    [Fact]
    public void ASectionLongerThanTheCharacterCapIsCutAndMarkedTruncated()
    {
        string markdown = "# Big\n" + new string('x', DocumentSectionIndex.MaxSectionCharacters * 2);

        IReadOnlyList<DocumentSection> sections = DocumentSectionIndex.Split(markdown);

        DocumentSection section = Assert.Single(sections);
        Assert.True(section.Truncated);
        Assert.True(section.Text.Length <= DocumentSectionIndex.MaxSectionCharacters);
    }

    /// <summary>A document over the size cap is never read whole: the selector fails safe and asks no question.</summary>
    [Fact]
    public async Task ADocumentOverTheSizeCapFailsSafeWithoutAskingTheProvider()
    {
        string path = Path.Combine(_temp.Path, "huge.md");
        await File.WriteAllTextAsync(path, "# Title\n" + new string('a', (int)SectionSelector.MaxDocumentBytes));
        UnaskedProvider provider = new();

        SectionSelection selection = await new SectionSelector(provider).SelectAsync(path, "Change the install steps.");

        Assert.True(selection.FailedSafe);
        Assert.Equal(0, provider.CallCount);
    }
}
