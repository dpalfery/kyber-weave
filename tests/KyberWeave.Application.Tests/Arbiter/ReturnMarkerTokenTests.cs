using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// A status word in a worker's digest is read without the punctuation or emphasis a model puts around it. Run 10
/// recorded a devops status of "Clean;" and run 14 a gate claim of "PASS,", and a word compared with its punctuation
/// attached matches nothing.
/// </summary>
public sealed class ReturnMarkerTokenTests
{
    [Theory]
    [InlineData("STATUS: PASS,", "PASS")]
    [InlineData("STATUS: **READY_FOR_REVIEW**", "READY_FOR_REVIEW")]
    [InlineData("STATUS: Clean;", "Clean")]
    [InlineData("STATUS: `PLAN_READY`.", "PLAN_READY")]
    [InlineData("STATUS: PASS", "PASS")]
    public void AStatusWordIsReadWithoutSurroundingPunctuation(string line, string expected)
    {
        ArbiterReturnMarkers? markers = ReturnMarkerParser.Parse(line + "\nTASK_GATE: PASS, agent=x report=/r.json", readAttestations: false);

        Assert.NotNull(markers);
        Assert.Equal(expected, markers.PlannerStatus);
        Assert.Equal("PASS", markers.TaskGate);
    }

    [Theory]
    [InlineData("TASK_GATE: PASS,", "PASS")]
    [InlineData("TASK_GATE: **PASS**", "PASS")]
    [InlineData("TASK_GATE: `PASS`", "PASS")]
    [InlineData("TASK_GATE: PASS\u2014agent=x", "PASS")]
    [InlineData("TASK_GATE: \u201cPASS\u201d", "PASS")]
    public void ATaskGateWordIsReadWithoutSurroundingPunctuation(string line, string expected)
    {
        ArbiterReturnMarkers? markers = ReturnMarkerParser.Parse(line, readAttestations: false);

        Assert.NotNull(markers);
        Assert.Equal(expected, markers.TaskGate);
    }
}
