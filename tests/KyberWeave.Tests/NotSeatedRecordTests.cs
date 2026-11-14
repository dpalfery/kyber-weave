using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The cycle's not-seated record: what <c>review seats</c> and the gate run decided, kept so <c>review verdict</c>
/// reads it instead of the reviewer retyping every reason.
/// </summary>
public sealed class NotSeatedRecordTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LaterDecisionsAreAddedToEarlierOnes()
    {
        string cycle = Path.Combine(_temp.Path, "review", "cycle-1");
        NotSeatedRecord.Add(cycle, new Dictionary<string, string> { ["infra-workflow"] = "no infrastructure file" });
        NotSeatedRecord.Add(cycle, new Dictionary<string, string> { ["static-analysis-triage"] = "gates passed" });

        IReadOnlyDictionary<string, string> read = NotSeatedRecord.Read(cycle);

        Assert.Equal("no infrastructure file", read["infra-workflow"]);
        Assert.Equal("gates passed", read["static-analysis-triage"]);
    }

    [Fact]
    public void ARerunReplacesTheLensesItOwnsSoAStaleReasonDoesNotSurvive()
    {
        string cycle = Path.Combine(_temp.Path, "review", "cycle-1");
        NotSeatedRecord.Add(cycle, new Dictionary<string, string> { ["infra-workflow"] = "patterns", ["static-analysis-triage"] = "gates passed" });

        NotSeatedRecord.Replace(cycle, CouncilSeating.GateProvenLenses, new Dictionary<string, string>());

        IReadOnlyDictionary<string, string> read = NotSeatedRecord.Read(cycle);
        Assert.False(read.ContainsKey("static-analysis-triage"));
        Assert.Equal("patterns", read["infra-workflow"]);
    }

    [Fact]
    public void AMissingOrBrokenRecordReadsAsEmpty()
    {
        string cycle = Path.Combine(_temp.Path, "review", "cycle-1");
        Assert.Empty(NotSeatedRecord.Read(cycle));

        Directory.CreateDirectory(cycle);
        File.WriteAllText(Path.Combine(cycle, NotSeatedRecord.FileName), "{not json");
        Assert.Empty(NotSeatedRecord.Read(cycle));
    }

    [Fact]
    public void TheLatestCycleIsTheHighestNumberedOneWithSeats()
    {
        string review = Path.Combine(_temp.Path, "review");
        Directory.CreateDirectory(Path.Combine(review, "cycle-2", "seats"));
        Directory.CreateDirectory(Path.Combine(review, "cycle-10", "seats"));
        Directory.CreateDirectory(Path.Combine(review, "cycle-11"));

        Assert.Equal(Path.Combine(review, "cycle-10"), NotSeatedRecord.LatestCycle(review));
        Assert.Null(NotSeatedRecord.LatestCycle(Path.Combine(_temp.Path, "none")));
    }
}
