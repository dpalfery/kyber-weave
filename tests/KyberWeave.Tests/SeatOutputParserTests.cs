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
    /// Unparseable YAML is refused with a typed error that names the reason, so the verdict command can report
    /// KW-REVIEW-040 with the file. The typed exception is not in the skeleton yet; the assertion rejects the
    /// placeholder NotImplementedException so the fact fails until the parser is written.
    /// </summary>
    [Fact]
    public void UnparseableYamlThrowsATypedErrorCarryingTheReason()
    {
        const string broken = "- id: [unclosed\n  lens: correctness\n";

        Exception error = Assert.ThrowsAny<Exception>(() => SeatOutputParser.Parse("correctness", broken));

        Assert.IsNotType<NotImplementedException>(error);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }
}
