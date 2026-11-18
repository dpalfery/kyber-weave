using KyberWeave.Tests.Configuration;

using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every coding standard this repository declares carries the six headings the ontology
/// requires, in order, including <c>## Done checklist</c> and <c>## Commands</c>.
/// </summary>
/// <remarks>
/// <para>
/// A finishing worker needs one place to learn how to prove its work: the inner-loop
/// commands, what runs when, and why. That knowledge lives in the standard's Done checklist,
/// reached by topic only when a worker is finishing. The Commands section answers the
/// different question of how to run the work, so both exist rather than one replacing the
/// other.
/// </para>
/// <para>
/// The standards are enumerated from <c>ontology.technologies</c> in
/// <c>.kyber-weave/kyber-weave.yml</c>, the repository's own registry, so a technology added
/// later cannot silently ship without one.
/// </para>
/// </remarks>
public sealed class StandardsCompletionSectionTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly string[] SixHeadings =
    [
        "## Naming and layout",
        "## Coding standards",
        "## Testing",
        "## Commands",
        "## Security and data handling",
        "## Done checklist"
    ];

    /// <summary>A legacy folder path that no live document may still link to.</summary>
    private const string LegacyPath = "standards/test/README.md";

    /// <summary>One argument per declared technology, read from the configuration rather than listed here.</summary>
    public static IEnumerable<object[]> DeclaredTechnologies() =>
        DeclaredStandards().Select(technology => new object[] { technology });

    [Theory]
    [MemberData(nameof(DeclaredTechnologies))]
    public void EveryDeclaredStandardCarriesTheSixHeadingsInOrder(string technology)
    {
        string path = StandardPath(technology);

        Assert.True(Corpus.Exists(path), $"The configuration declares technology '{technology}' and '{path}' does not exist.");
        IReadOnlyList<string> headings = Headings(Corpus.ReadText(path));

        Assert.Equal(SixHeadings, headings);
    }

    [Theory]
    [MemberData(nameof(DeclaredTechnologies))]
    public void EveryDeclaredStandardCarriesOneDoneChecklist(string technology)
    {
        int count = Headings(Corpus.ReadText(StandardPath(technology)))
            .Count(heading => heading == "## Done checklist");

        Assert.True(
            count == 1,
            $"The '{technology}' coding standard declares {count} '## Done checklist' sections. " +
            "Add exactly one: it carries the inner-loop commands a finishing worker runs.");
    }

    /// <summary>
    /// No live document still links to the pre-migration <c>README.md</c> standards path.
    /// </summary>
    /// <remarks>
    /// The migration renamed each standard to its prefixed file. A link left on the old path
    /// is dead, and nothing reports it: Markdown links to a missing file are not validated
    /// here, so the reader lands on nothing. Archived plans are excluded because they record
    /// what was true when they were written and are never updated.
    /// </remarks>
    [Fact]
    public void NoLiveDocumentLinksToALegacyStandardsReadme()
    {
        string[] offenders = LiveMarkdown()
            .SelectMany(file => Corpus.ReadText(file).ReplaceLineEndings("\n").Split('\n')
                .Select((line, index) => (File: file, Line: index + 1, Text: line))
                .Where(row => row.Text.Contains(LegacyPath, StringComparison.Ordinal))
                .Select(row => $"{row.File}:{row.Line}: {row.Text.Trim()}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These lines link to '{LegacyPath}', which the migration renamed. Point them at " +
            "'standards/test/test.standards.md':" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static IReadOnlyList<string> DeclaredStandards()
    {
        return ConfigFileReader.Load(Corpus.Root).Ontology.Technologies;
    }

    private static string StandardPath(string technology)
    {
        return $"docs/standards/{technology}/{technology}.standards.md";
    }

    /// <summary>The level-two headings of a Markdown document, hashes included.</summary>
    private static IReadOnlyList<string> Headings(string markdown)
    {
        return markdown
            .Split('\n')
            .Select(line => line.TrimEnd('\r').Trim())
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// Every Markdown document that is current guidance: the repository root files, everything under
    /// <c>docs/</c> except the archive, and the portable roster.
    /// </summary>
    private static IEnumerable<string> LiveMarkdown()
    {
        IEnumerable<string> roots = ["AGENTS.md", "README.md", "REVIEW.md", "CONTRIBUTING.md", "SECURITY.md"];

        foreach (string file in roots.Where(Corpus.Exists))
            yield return file;

        foreach (string directory in new[] { "docs", "products", ".apm" })
        {
            foreach (string file in Corpus.EnumerateFiles(directory, "*.md"))
            {
                if (file.Split('/').Contains("archive", StringComparer.Ordinal))
                    continue;

                yield return file;
            }
        }
    }
}
