using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every coding standard this repository declares carries a <c>## Completion</c> section, and the test standard's
/// former <c>## Commands</c> section is now that section.
/// </summary>
/// <remarks>
/// <para>
/// A finishing worker needs one place to learn how to prove its work: the inner-loop commands, what runs when, and
/// why. That knowledge used to live in the portable agent files, which sent it to every host on every round trip
/// and let it drift per project. D5 moves it into the standard, reached by topic only when a worker is finishing;
/// D31 renames the test standard's <c>## Commands</c> section to it rather than adding a second section that would
/// answer the same question.
/// </para>
/// <para>
/// The standards are enumerated from <c>ontology.technologies</c> in <c>.kyber-weave/kyber-weave.yml</c>, the
/// repository's own registry, so a technology added later cannot silently ship without one.
/// </para>
/// </remarks>
public sealed class StandardsCompletionSectionTests
{
    private const string CommandsHeading = "## Commands";

    /// <summary>The test standard's retired anchor, as every link to it is written.</summary>
    private const string Anchor = "standards/test/README.md#commands";

    /// <summary>One argument per declared technology, read from the configuration rather than listed here.</summary>
    public static IEnumerable<object[]> DeclaredTechnologies() =>
        DeclaredStandards().Select(technology => new object[] { technology });

    /// <summary>Each declared standard carries exactly one <c>## Completion</c> section.</summary>
    [Theory]
    [MemberData(nameof(DeclaredTechnologies))]
    public void EveryDeclaredStandardCarriesOneCompletionSection(string technology)
    {
        string path = StandardPath(technology);

        Assert.True(File.Exists(path), $"The configuration declares technology '{technology}' and '{path}' does not exist.");
        int count = Headings(File.ReadAllText(path)).Count(heading => heading == "## Completion");

        Assert.True(
            count == 1,
            $"The '{technology}' coding standard declares {count} '## Completion' sections. Add exactly one: it " +
            "carries the inner-loop commands a finishing worker runs, what runs when, and why.");
    }

    /// <summary>
    /// No declared standard keeps a <c>## Commands</c> section beside the completion section.
    /// </summary>
    /// <remarks>
    /// Two sections answering the same question is how the answer drifts apart: one is edited, the other is not,
    /// and the worker reads whichever it loads. D31 renames the section rather than adding a second one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DeclaredTechnologies))]
    public void NoDeclaredStandardKeepsACommandsSection(string technology)
    {
        Assert.DoesNotContain(CommandsHeading, Headings(File.ReadAllText(StandardPath(technology))), StringComparer.Ordinal);
    }

    /// <summary>
    /// No live document links to the test standard's retired <c>#commands</c> anchor.
    /// </summary>
    /// <remarks>
    /// Renaming the heading moves its anchor. A link left pointing at the old one lands on the top of the document
    /// with no sign that it missed, which reads as "the anchor is here" to everyone who follows it. Archived plans
    /// are excluded because they record what was true when they were written and are never updated.
    /// </remarks>
    [Fact]
    public void NoLiveDocumentLinksToTheRetiredTestStandardCommandsAnchor()
    {
        string[] offenders = LiveMarkdown()
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (File: file, Line: index + 1, Text: line))
                .Where(row => row.Text.Contains(Anchor, StringComparison.Ordinal))
                .Select(row => $"{Relative(row.File)}:{row.Line}: {row.Text.Trim()}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These lines link to '{Anchor}', which no longer exists. Point them at 'standards/test/README.md#" +
            "completion':" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static IReadOnlyList<string> DeclaredStandards()
    {
        return KyberWeaveConfigLoader.Load(KyberWeaveTestPaths.ToolRoot).Ontology.Technologies;
    }

    private static string StandardPath(string technology)
    {
        return Path.Combine(KyberWeaveTestPaths.ToolRoot, "docs", "standards", technology, "README.md");
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
        string root = KyberWeaveTestPaths.ToolRoot;
        IEnumerable<string> roots =
        [
            Path.Combine(root, "AGENTS.md"),
            Path.Combine(root, "README.md"),
            Path.Combine(root, "REVIEW.md"),
            Path.Combine(root, "CONTRIBUTING.md"),
            Path.Combine(root, "SECURITY.md")
        ];

        foreach (string file in roots.Where(File.Exists))
            yield return file;

        foreach (string directory in new[] { Path.Combine(root, "docs"), Path.Combine(root, "products"), Path.Combine(root, ".apm") })
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories))
            {
                if (file.Split(Path.DirectorySeparatorChar).Contains("archive", StringComparer.Ordinal))
                    continue;

                yield return file;
            }
        }
    }

    private static string Relative(string path)
    {
        return Path.GetRelativePath(KyberWeaveTestPaths.ToolRoot, path).Replace('\\', '/');
    }
}
