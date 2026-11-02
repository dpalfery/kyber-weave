using System.Text.Json;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Review documents are the seam between an agentic layer and a deterministic one. A
/// missing required constructor parameter must fail the parse rather than silently
/// default — that is the whole of <c>RespectRequiredConstructorParameters</c>.
/// </summary>
public sealed class ReviewJsonTests
{
    [Fact]
    public void AnEmptyFindingsObjectIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadFindings("{}"));
    }

    [Fact]
    public void AnEmptyGatesObjectIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadGates("{}"));
    }

    [Fact]
    public void AnEmptyDuplicatesObjectIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadDuplicates("{}"));
    }

    [Fact]
    public void AFindingsReportMissingRequiredConstructorParametersIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadFindings(
            """{"schema":"kyber-weave.review-findings/v1","findings":[],"changedPaths":[]}"""));
    }

    [Fact]
    public void AGateReportWhoseGateOmitsRequiredConstructorParametersIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadGates(
            """{"schema":"kyber-weave.review-gates/v1","gates":[{}]}"""));
    }

    [Fact]
    public void ADuplicatesReportMissingRequiredConstructorParametersIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadDuplicates(
            """{"schema":"kyber-weave.review-duplicates/v1","clusters":[]}"""));
    }

    [Fact]
    public void AFindingThatOmitsRequiredConstructorParametersIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadFindings(
            """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [{}],
              "changedPaths": ["src/Foo.cs"],
              "changedLines": 1
            }
            """));
    }

    /// <summary>
    /// A gate that never started must be distinguishable in the JSON from one that ran and failed,
    /// so a reader of the report can tell a missing tool from a failing check without parsing the summary.
    /// </summary>
    [Fact]
    public void AGateReportWritesEachGatesOutcomeAsAnAddedField()
    {
        GateReport report = new(
            GateReport.CurrentSchema,
            [
                new GateResult("missing-tool", true, -1, "could not start 'kw-tool'", 3, GateOutcome.NotStarted),
                new GateResult("slow-gate", true, -1, "timed out after 5 seconds", 5000, GateOutcome.TimedOut),
                new GateResult("tests", true, 1, "2 failed", 900, GateOutcome.Completed)
            ]);

        using JsonDocument document = JsonDocument.Parse(ReviewJson.Write(report));
        JsonElement gates = document.RootElement.GetProperty("gates");

        Assert.Equal("notStarted", gates[0].GetProperty("outcome").GetString());
        Assert.Equal("timedOut", gates[1].GetProperty("outcome").GetString());
        Assert.Equal("completed", gates[2].GetProperty("outcome").GetString());
    }

    [Fact]
    public void AGateReportKeepsItsSchemaVersionWhenOutcomeIsAdded()
    {
        GateReport report = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "passed")]);

        using JsonDocument document = JsonDocument.Parse(ReviewJson.Write(report));

        Assert.Equal("kyber-weave.review-gates/v1", document.RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public void AGateReportWrittenBeforeOutcomeExistedStillReads()
    {
        GateReport read = ReviewJson.ReadGates(
            """{"schema":"kyber-weave.review-gates/v1","gates":[{"id":"build","blocking":true,"exitCode":0,"summary":"passed"}]}""");

        GateResult gate = Assert.Single(read.Gates);
        Assert.Equal("build", gate.Id);
        Assert.True(gate.Passed);
    }

    [Fact]
    public void AGateOutcomeSurvivesAWriteAndRead()
    {
        GateReport report = new(
            GateReport.CurrentSchema,
            [new GateResult("missing-tool", true, -1, "could not start 'kw-tool'", 3, GateOutcome.NotStarted)]);

        GateReport read = ReviewJson.ReadGates(ReviewJson.Write(report));

        Assert.Equal(GateOutcome.NotStarted, Assert.Single(read.Gates).Outcome);
    }
}
