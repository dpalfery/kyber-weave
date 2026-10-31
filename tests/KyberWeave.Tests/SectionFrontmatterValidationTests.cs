using System.Text;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     T13 RED — optional <c>lead</c> and <c>sections</c> frontmatter, roll-up to document
///     keywords and code-refs, and validators <c>KW-DOC-SPEC-008</c> through <c>-010</c> plus
///     extensions to <c>KW-DOC-SPEC-002</c> and <c>KW-DOC-DRIFT-001</c>.
/// </summary>
public sealed class SectionFrontmatterValidationTests
{
    private const string UnknownSectionSlug = "KW-DOC-SPEC-008";
    private const string LeadExceedsAnswerCeiling = "KW-DOC-SPEC-009";
    private const string UngroundedSectionKeyword = "KW-DOC-SPEC-010";

    private const string PreambleMarker = "PREAMBLE-LEAD-MARKER-XYZZY";
    private const string DetailMarker = "DETAIL-LEAD-MARKER-ABCDE";

    private const string SubjectPath = "6-Docs/reference/section-fixture.md";

    [Fact]
    public void SectionKeywordsAndCodeRefsRollUpToTheDocumentView()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  "#":
                    keywords:
                      - rolled-preamble-alias
                  detail:
                    keywords:
                      - rolled-section-alias
                    code-refs:
                      - RolledSectionSymbol
                """,
                """
                ## Topic

                ### Detail

                Section body.
                """));

        DocumentModel doc = Assert.Single(fixture.LoadSubjects());

        Assert.Contains("rolled-preamble-alias", doc.Keywords);
        Assert.Contains("rolled-section-alias", doc.Keywords);
        Assert.Contains("RolledSectionSymbol", doc.CodeRefs);
    }

    [Fact]
    public void AbsentLeadReturnsPreambleProseOnExactDocumentIdQuery()
    {
        using DocFixture fixture = BuildLeadFixture(null);
        DocumentIndex index = BuildIndex(fixture);

        ExploreAnswer answer = index.Explore("fixture/lead-target");

        Assert.False(answer.IsMiss);
        Assert.Contains(PreambleMarker, answer.Prose, StringComparison.Ordinal);
        Assert.DoesNotContain(DetailMarker, answer.Prose, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredLeadReturnsThatLeafOnExactDocumentIdQuery()
    {
        using DocFixture fixture = BuildLeadFixture("lead: detail");
        DocumentIndex index = BuildIndex(fixture);

        ExploreAnswer answer = index.Explore("fixture/lead-target");

        Assert.False(answer.IsMiss);
        Assert.Contains(DetailMarker, answer.Prose, StringComparison.Ordinal);
        Assert.DoesNotContain(PreambleMarker, answer.Prose, StringComparison.Ordinal);
    }

    [Fact]
    public void LeadNamingMissingSlugIsSPEC008WithNearestSlugHint()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                "lead: almost-detail",
                """
                ## Topic

                ### Detail

                Body.
                """));

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(
            report.Items,
            i => i.Code == UnknownSectionSlug && i.Severity == Severity.Error);
        Assert.Contains("almost-detail", item.Message, StringComparison.Ordinal);
        Assert.Contains("detail", item.Hint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SectionsKeyNamingMissingSlugIsSPEC008WithNearestSlugHint()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  almost-detail:
                    keywords:
                      - orphan-alias
                """,
                """
                ## Topic

                ### Detail

                Body.
                """));

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(
            report.Items,
            i => i.Code == UnknownSectionSlug && i.Severity == Severity.Error);
        Assert.Contains("almost-detail", item.Message, StringComparison.Ordinal);
        Assert.Contains("detail", item.Hint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeadLeafLongerThan24000CharactersIsSPEC009()
    {
        string huge = Repeat('x', ExploreAnswer.AnswerCharCap + 1);
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                "lead: huge-leaf",
                $"""
                 ## Topic

                 ### Huge leaf

                 {huge}
                 """));

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(
            report.Items,
            i => i.Code == LeadExceedsAnswerCeiling && i.Severity == Severity.Error);
        Assert.Contains("huge-leaf", item.Message, StringComparison.Ordinal);
        Assert.Contains("24000", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionKeywordWithNoWordsInThatSectionIsSPEC010Warning()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  detail:
                    keywords:
                      - zzzorphan
                """,
                """
                ## Topic

                ### Detail

                alpha beta gamma.
                """));

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(
            report.Items,
            i => i.Code == UngroundedSectionKeyword && i.Severity == Severity.Warning);
        Assert.Contains("zzzorphan", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySectionKeywordEntryIsSPEC002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  detail:
                    keywords:
                      - ""
                """,
                """
                ## Topic

                ### Detail

                alpha beta gamma.
                """));

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(report.Items, i => i.Code == DocSpecValidator.InvalidVocabulary);
        Assert.Equal("Each keyword must be a non-empty string.", item.Hint);
        Assert.Contains("detail", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySectionCodeRefEntryIsSPEC002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  detail:
                    code-refs:
                      - ""
                """,
                """
                ## Topic

                ### Detail

                alpha beta gamma.
                """));

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(report.Items, i => i.Code == DocSpecValidator.InvalidVocabulary);
        Assert.Equal("Each code-refs entry must be a non-empty string.", item.Hint);
        Assert.Contains("detail", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedSectionCodeRefIsDRIFT001()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  detail:
                    code-refs:
                      - MissingSectionSymbol
                """,
                """
                ## Topic

                ### Detail

                alpha beta gamma.
                """));

        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols();
        DiagnosticReport report = new DocDriftLinter(resolver).Validate(fixture.Load());

        Diagnostic item = Assert.Single(
            report.Items,
            i => i.Code == DocDriftLinter.UnresolvedCodeRef && i.Severity == Severity.Error);
        Assert.Contains("MissingSectionSymbol", item.Message, StringComparison.Ordinal);
        Assert.Contains("detail", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionsDeclaredAsScalarSurfacesInvalidVocabulary()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  detail:
                    keywords: not-a-list
                """,
                """
                ## Topic

                ### Detail

                Body.
                """));

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(
            report.Items,
            i => i.Code == DocSpecValidator.InvalidVocabulary && i.Severity == Severity.Error);
        Assert.Contains("keywords", item.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullSectionEntryDoesNotThrowOnLoad()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  install:
                """,
                """
                ## Topic

                ### Detail

                Body.
                """));

        DocumentModel doc = Assert.Single(fixture.LoadSubjects());
        Assert.NotNull(doc.Keywords);
    }

    [Fact]
    public void SectionValueThatIsASequenceNamesTheMappingRequirement()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                """
                sections:
                  install:
                    - a
                    - b
                """,
                """
                ## Topic

                ### Detail

                Body.
                """));

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(
            report.Items,
            i => i.Code == DocSpecValidator.InvalidVocabulary && i.Severity == Severity.Error);
        Assert.Contains("must be a mapping", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneBrokenYamlBlockReportsASingleParseError()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(SubjectPath, ReferenceBody(
                "sections: [",
                """
                ## Topic

                ### Detail

                Body.
                """));

        DiagnosticReport report = fixture.Validate();

        Assert.Single(report.Items, i => i.Code == DocSpecValidator.MissingFrontmatter);
        Assert.DoesNotContain(
            report.Items,
            i => i.Message.Contains("Section frontmatter could not be parsed", StringComparison.Ordinal));
    }

    private static DocFixture BuildLeadFixture(string? leadYaml)
    {
        DocFixture fixture = new DocFixture().WithCatalog();
        string extra = leadYaml is null ? string.Empty : leadYaml + Environment.NewLine;
        fixture.Write(SubjectPath, ReferenceBody(extra, $"""
                                                         {PreambleMarker}

                                                         ## Topic

                                                         ### Detail

                                                         {DetailMarker}
                                                         """));
        return fixture;
    }

    private static string ReferenceBody(string extraFrontmatter, string body)
    {
        StringBuilder text = new();
        text.AppendLine("---");
        text.AppendLine("id: fixture/lead-target");
        text.AppendLine("title: Lead fixture");
        text.AppendLine("doc-type: reference");
        text.AppendLine("status: current");
        text.AppendLine("owner: Maintainers");
        text.AppendLine("last-reviewed: 2026-07-21");
        foreach (string line in extraFrontmatter.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            text.AppendLine(line.TrimEnd());
        }

        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine("# Lead fixture");
        text.AppendLine();
        text.AppendLine(body.Trim());
        return text.ToString();
    }

    private static DocumentIndex BuildIndex(DocFixture fixture)
    {
        return DocumentIndex.Build(fixture.Load(), FakeCodeGraphResolver.WithSymbols());
    }

    private static string Repeat(char value, int count)
    {
        StringBuilder builder = new(count);
        builder.Append(value, count);
        return builder.ToString();
    }
}
