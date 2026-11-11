using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A council seat writes its lens's output as text, not as the JSON the verdict engine used to demand.
/// These facts pin what the parser reads from the seat transcripts that really ran, so a seat's
/// honest output (no lens field, no claim, a snake_case failure scenario) is never silently dropped.
/// The fixtures are the final assistant text of real seat runs, copied unchanged.
/// </summary>
public sealed class SeatOutputParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "review-seats",
            name));

    [Theory]
    [InlineData("589-prior-art-performance.md", "prior-art-performance", 5, SeatOutputKind.Findings)]
    [InlineData("589-blast-radius.md", "blast-radius", 3, SeatOutputKind.Findings)]
    [InlineData("590-documentation-accuracy.md", "documentation-accuracy", 2, SeatOutputKind.Findings)]
    [InlineData("591-correctness.md", "correctness", 3, SeatOutputKind.Findings)]
    [InlineData("591-security.md", "security", 0, SeatOutputKind.NoFindings)]
    public void EachRealSeatFixtureParsesToItsFindingsAndKind(
        string fileName,
        string lens,
        int expectedFindings,
        SeatOutputKind expectedKind)
    {
        SeatOutput seat = SeatOutputParser.Parse(lens, Fixture(fileName));

        Assert.Equal(expectedKind, seat.Kind);
        Assert.Equal(expectedFindings, seat.Findings.Count);
        Assert.Equal(lens, seat.Lens);
    }

    [Fact]
    public void SeverityAndConfidenceAreTheValuesTheSeatWrote()
    {
        SeatOutput seat = SeatOutputParser.Parse("prior-art-performance", Fixture("589-prior-art-performance.md"));

        Assert.Equal(ReviewSeverity.Major, seat.Findings[0].Severity);
        Assert.Equal(8, seat.Findings[0].Confidence);
        Assert.Equal(ReviewSeverity.Minor, seat.Findings[4].Severity);
        Assert.Equal(7, seat.Findings[4].Confidence);
    }

    /// <summary>
    /// The documentation-accuracy seat omits <c>lens</c> from every finding. The id prefix before the slash
    /// names the lens, so the finding is still attributable and the seat is not refused for a missing field.
    /// </summary>
    [Fact]
    public void AMissingLensTakesTheIdPrefixBeforeTheSlash()
    {
        SeatOutput seat = SeatOutputParser.Parse("documentation-accuracy", Fixture("590-documentation-accuracy.md"));

        Assert.All(seat.Findings, finding => Assert.Equal("documentation-accuracy", finding.Lens));
    }

    /// <summary>
    /// The blast-radius and documentation seats omit <c>claim</c>. A missing claim is allowed at parse time;
    /// the verdict engine decides what an incomplete finding is worth, not the parser.
    /// </summary>
    [Fact]
    public void AMissingClaimIsAllowedAndReadsAsAbsent()
    {
        SeatOutput seat = SeatOutputParser.Parse("prior-art-performance", Fixture("589-prior-art-performance.md"));

        Assert.All(seat.Findings, finding => Assert.True(string.IsNullOrWhiteSpace(finding.Claim)));
    }

    [Fact]
    public void FailureScenarioLandsInTheFailureScenarioProperty()
    {
        SeatOutput seat = SeatOutputParser.Parse("documentation-accuracy", Fixture("590-documentation-accuracy.md"));

        Assert.NotNull(seat.Findings[0].FailureScenario);
        Assert.Contains("retargeted", seat.Findings[0].FailureScenario, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedSeatIsRecognisedWithNoFindingsAndNoAttestation()
    {
        SeatOutput seat = SeatOutputParser.Parse("performance", Fixture("skipped.md"));

        Assert.Equal(SeatOutputKind.Skipped, seat.Kind);
        Assert.Empty(seat.Findings);
        Assert.Null(seat.Attestation);
    }

    [Fact]
    public void NoFindingsSeatIsRecognisedAndKeepsItsAttestation()
    {
        SeatOutput seat = SeatOutputParser.Parse("security", Fixture("591-security.md"));

        Assert.Equal(SeatOutputKind.NoFindings, seat.Kind);
        Assert.Empty(seat.Findings);
        Assert.NotNull(seat.Attestation);
        Assert.Contains("REVIEW.md sections checked", seat.Attestation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The closing lines are read in the order a seat writes them. Index fallback and discovery counts are
    /// what the verdict engine checks for KW-REVIEW-044, so they must come through exactly as written.
    /// </summary>
    [Fact]
    public void AttestationIndexFallbackAndDiscoveryCountsAreRead()
    {
        SeatOutput seat = SeatOutputParser.Parse("security", Fixture("with-discovery.md"));

        Assert.Equal(2, seat.Findings.Count);
        Assert.NotNull(seat.Attestation);
        Assert.Contains("REVIEW.md sections checked", seat.Attestation, StringComparison.Ordinal);
        Assert.Equal("projectPath: not-indexed", seat.IndexFallback);
        Assert.Equal(new SeatDiscovery(CodeGraph: 0, Docs: 0, DirectReads: 6), seat.Discovery);
    }

    /// <summary>
    /// A seat often wraps its YAML in a code fence and sometimes does not. Both must yield the same findings.
    /// </summary>
    [Fact]
    public void FencedAndUnfencedYamlParseToTheSameFindings()
    {
        string fenced = Fixture("589-blast-radius.md");
        string unfenced = string.Join(
            '\n',
            fenced.Split('\n').Where(line => !line.StartsWith("```", StringComparison.Ordinal)));

        SeatOutput fromFenced = SeatOutputParser.Parse("blast-radius", fenced);
        SeatOutput fromUnfenced = SeatOutputParser.Parse("blast-radius", unfenced);

        Assert.Equal(
            fromFenced.Findings.Select(finding => finding.Id),
            fromUnfenced.Findings.Select(finding => finding.Id));
    }

    /// <summary>
    /// Unparseable YAML is refused with the typed <see cref="SeatOutputException"/> that names the reason, so the
    /// verdict command can report KW-REVIEW-040 with the file.
    /// </summary>
    [Fact]
    public void UnparseableYamlThrowsATypedErrorCarryingTheReason()
    {
        const string broken = "- id: [unclosed\n  lens: correctness\n";

        SeatOutputException error = Assert.Throws<SeatOutputException>(
            () => SeatOutputParser.Parse("correctness", broken));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    // -----------------------------------------------------------------------------------------
    // The seats as they were written, before the parser was made tolerant of the flat schema.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheCorrectnessSeatAsItWroteItParsesWithItsFourSeverities()
    {
        SeatOutput seat = SeatOutputParser.Parse("correctness", Fixture("correctness-flat.md"));

        Assert.Equal(SeatOutputKind.Findings, seat.Kind);
        Assert.Equal(
            [ReviewSeverity.Major, ReviewSeverity.Major, ReviewSeverity.Minor, ReviewSeverity.Minor],
            seat.Findings.Select(finding => finding.Severity));
        Assert.Equal(new SeatDiscovery(CodeGraph: 1, Docs: 0, DirectReads: 9), seat.Discovery);
    }

    [Fact]
    public void TheSupportabilitySeatAsItWroteItParsesWithItsFourSeverities()
    {
        SeatOutput seat = SeatOutputParser.Parse("supportability", Fixture("supportability-flat.md"));

        Assert.Equal(SeatOutputKind.Findings, seat.Kind);
        Assert.Equal(
            [ReviewSeverity.Minor, ReviewSeverity.Minor, ReviewSeverity.Major, ReviewSeverity.Minor],
            seat.Findings.Select(finding => finding.Severity));
        Assert.Equal(new SeatDiscovery(CodeGraph: 0, Docs: 0, DirectReads: 8), seat.Discovery);
    }

    [Fact]
    public void TheTestAdequacySeatAsItWroteItParsesWithItsSevenFindingsAndUnknownFallbackMode()
    {
        SeatOutput seat = SeatOutputParser.Parse("test-adequacy", Fixture("test-adequacy-flat.md"));

        Assert.Equal(SeatOutputKind.Findings, seat.Kind);
        Assert.Equal(
            [
                ReviewSeverity.Major, ReviewSeverity.Major, ReviewSeverity.Minor, ReviewSeverity.Major,
                ReviewSeverity.Major, ReviewSeverity.Minor, ReviewSeverity.Minor
            ],
            seat.Findings.Select(finding => finding.Severity));
        Assert.NotNull(seat.IndexFallback);
        Assert.StartsWith("not-used", seat.IndexFallback, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Flat-schema tolerance: a plain scalar a seat really writes must read as the text it wrote.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void APlainScalarStartingWithABacktickIsReadAsWritten()
    {
        const string claim = "`Foo.Bar` returns null for empty input.";

        SeatOutput seat = SeatOutputParser.Parse("correctness", Fenced(FindingLines(claim: claim)));

        Assert.Equal(claim, Assert.Single(seat.Findings).Claim);
    }

    [Fact]
    public void APlainScalarContainingColonSpaceIsReadAsWritten()
    {
        const string claim = "The rule says: never drop it.";

        SeatOutput seat = SeatOutputParser.Parse("correctness", Fenced(FindingLines(claim: claim)));

        Assert.Equal(claim, Assert.Single(seat.Findings).Claim);
    }

    /// <summary>
    /// A refusal names where the YAML went wrong. The reviewer fixing a seat file needs a line and a column, not
    /// just "could not be read".
    /// </summary>
    [Fact]
    public void AnUnreadableSeatNamesTheLineAndColumnOfTheFault()
    {
        // The second list item is a bare scalar, not a finding mapping, so the fault is on the third line of the YAML.
        string text = Fenced(["- id: correctness/a", "  lens: correctness", "- just a string"]);

        SeatOutputException error = Assert.Throws<SeatOutputException>(
            () => SeatOutputParser.Parse("correctness", text));

        Assert.Matches(@"line \d+", error.Message);
        Assert.Matches(@"column \d+", error.Message);
    }

    // -----------------------------------------------------------------------------------------
    // A file that declares an outcome and also holds findings is contradictory, so it is refused.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("NO FINDINGS: I checked the lens and found nothing.")]
    [InlineData("SKIPPED: the lens could not run in this session.")]
    public void ADeclaredOutcomeWithFindingsInTheSameFileIsRefused(string declared)
    {
        string text = declared + "\n" + Fenced(FindingLines());

        Assert.Throws<SeatOutputException>(() => SeatOutputParser.Parse("correctness", text));
    }

    // -----------------------------------------------------------------------------------------
    // Fences: only a column-0 fence delimits the YAML; a fence quoted inside an excerpt is content.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AnIndentedFenceInsideAnExcerptDoesNotDropTheLaterFindings()
    {
        List<string> first =
        [
            "- id: correctness/first",
            "  lens: correctness",
            "  severity: minor",
            "  confidence: 7",
            "  file: src/Foo.cs",
            "  line: 3",
            "  excerpt: |",
            "    ```csharp",
            "    int x = 1;",
            "  claim: The first finding quotes a fence inside its excerpt."
        ];
        List<string> body = [.. first, .. FindingLines(id: "correctness/second")];

        SeatOutput seat = SeatOutputParser.Parse("correctness", Fenced(body));

        Assert.Equal(["correctness/first", "correctness/second"], seat.Findings.Select(finding => finding.Id));
    }

    [Fact]
    public void AFourBacktickFenceAroundTheYamlParsesItsFindings()
    {
        SeatOutput seat = SeatOutputParser.Parse("correctness", Fenced(FindingLines(), fence: "````"));

        Assert.Equal(SeatOutputKind.Findings, seat.Kind);
        Assert.Equal(["correctness/off-by-one"], seat.Findings.Select(finding => finding.Id));
    }

    // -----------------------------------------------------------------------------------------
    // Malformed input is refused with the field named, never read as a finding with a nonsense value.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ANumericSeverityIsRefusedNamingTheField()
    {
        SeatOutputException error = Assert.Throws<SeatOutputException>(
            () => SeatOutputParser.Parse("correctness", Fenced(FindingLines(severity: "2"))));

        Assert.Contains("severity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSeverityWordIsRefusedNamingTheField()
    {
        SeatOutputException error = Assert.Throws<SeatOutputException>(
            () => SeatOutputParser.Parse("correctness", Fenced(FindingLines(severity: "blocker"))));

        Assert.Contains("severity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfidenceAboveTenIsRefusedNamingTheField()
    {
        SeatOutputException error = Assert.Throws<SeatOutputException>(
            () => SeatOutputParser.Parse("correctness", Fenced(FindingLines(confidence: "11"))));

        Assert.Contains("confidence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonNumericConfidenceIsRefusedNamingTheField()
    {
        SeatOutputException error = Assert.Throws<SeatOutputException>(
            () => SeatOutputParser.Parse("correctness", Fenced(FindingLines(confidence: "high"))));

        Assert.Contains("confidence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFindingWithNoIdIsRefusedNamingTheField()
    {
        SeatOutputException error = Assert.Throws<SeatOutputException>(
            () => SeatOutputParser.Parse("correctness", Fenced(FindingLines(id: null))));

        Assert.Contains("id", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    public void AnEmptySeatIsRefusedAsHavingNoOutcome(string text)
    {
        Assert.Throws<SeatOutputException>(() => SeatOutputParser.Parse("correctness", text));
    }

    /// <summary>
    /// A seat on Windows writes CRLF line endings. The same findings must read the same whichever way the file ends its lines.
    /// </summary>
    [Fact]
    public void ACrlfSeatReadsTheSameFindingsAsItsLfForm()
    {
        string lf = Fenced(FindingLines());

        SeatOutput fromLf = SeatOutputParser.Parse("correctness", lf);
        SeatOutput fromCrlf = SeatOutputParser.Parse("correctness", lf.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(
            fromLf.Findings.Select(finding => (finding.Id, finding.Severity, finding.Confidence, finding.Claim)),
            fromCrlf.Findings.Select(finding => (finding.Id, finding.Severity, finding.Confidence, finding.Claim)));
    }

    /// <summary>
    /// A seat may write more than one YAML document. The parser must read every document or refuse the file;
    /// silently reading the first and dropping the rest is the defect this fact guards against.
    /// </summary>
    [Fact]
    public void AMultiDocumentSeatIsReadInFullOrRefused()
    {
        List<string> body =
        [
            .. FindingLines(id: "correctness/first"),
            "---",
            .. FindingLines(id: "correctness/second")
        ];

        try
        {
            SeatOutput seat = SeatOutputParser.Parse("correctness", Fenced(body));
            Assert.Equal(2, seat.Findings.Count);
        }
        catch (SeatOutputException)
        {
            // Refusing the file is the other allowed outcome.
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers: seat text built from the parts a seat writes.
    // -----------------------------------------------------------------------------------------

    /// <summary>One finding's YAML list item. A null id or severity or confidence omits that line.</summary>
    private static List<string> FindingLines(
        string? id = "correctness/off-by-one",
        string? severity = "minor",
        string? confidence = "7",
        string claim = "The loop bound excludes the final element.")
    {
        List<string> lines = [];
        lines.Add(id is null ? "- lens: correctness" : $"- id: {id}");
        if (id is not null)
            lines.Add("  lens: correctness");
        if (severity is not null)
            lines.Add($"  severity: {severity}");
        if (confidence is not null)
            lines.Add($"  confidence: {confidence}");
        lines.Add("  file: src/Foo.cs");
        lines.Add("  line: 12");
        lines.Add("  excerpt: \"if (i < count)\"");
        lines.Add($"  claim: {claim}");
        lines.Add("  evidence: src/Foo.cs:12");
        lines.Add("  failure_scenario: The last element is never processed.");
        lines.Add("  suggestion: Use <= or count + 1.");
        return lines;
    }

    private static string Fenced(IEnumerable<string> body, string fence = "```")
    {
        List<string> lines = [fence + "yaml"];
        lines.AddRange(body);
        lines.Add(fence);
        return string.Join('\n', lines) + "\n";
    }
}
