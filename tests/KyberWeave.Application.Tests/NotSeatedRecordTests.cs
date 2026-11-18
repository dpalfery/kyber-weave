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
    private readonly InMemoryReviewServices _services = new();

    public void Dispose() => _services.Dispose();

    [Fact]
    public void LaterDecisionsAreAddedToEarlierOnes()
    {
        string cycle = Path.Combine(_services.Root, "review", "cycle-1");
        _services.NotSeated.Add(cycle, new Dictionary<string, string> { ["infra-workflow"] = "no infrastructure file" });
        _services.NotSeated.Add(cycle, new Dictionary<string, string> { ["static-analysis-triage"] = "gates passed" });

        IReadOnlyDictionary<string, string> read = _services.NotSeated.Read(cycle);

        Assert.Equal("no infrastructure file", read["infra-workflow"]);
        Assert.Equal("gates passed", read["static-analysis-triage"]);
    }

    [Fact]
    public void ARerunReplacesTheLensesItOwnsSoAStaleReasonDoesNotSurvive()
    {
        string cycle = Path.Combine(_services.Root, "review", "cycle-1");
        _services.NotSeated.Add(cycle, new Dictionary<string, string> { ["infra-workflow"] = "patterns", ["static-analysis-triage"] = "gates passed" });

        _services.NotSeated.Replace(cycle, CouncilSeating.GateProvenLenses, new Dictionary<string, string>());

        IReadOnlyDictionary<string, string> read = _services.NotSeated.Read(cycle);
        Assert.False(read.ContainsKey("static-analysis-triage"));
        Assert.Equal("patterns", read["infra-workflow"]);
    }

    [Fact]
    public void AMissingOrBrokenRecordReadsAsEmpty()
    {
        string cycle = Path.Combine(_services.Root, "review", "cycle-1");
        Assert.Empty(_services.NotSeated.Read(cycle));

        _services.Files.CreateDirectory(cycle);
        _services.Files.WriteAllText(Path.Combine(cycle, NotSeatedRecord.FileName), "{not json");
        Assert.Empty(_services.NotSeated.Read(cycle));
    }

    [Fact]
    public void TheLatestCycleIsTheHighestNumberedOneWithSeats()
    {
        string review = Path.Combine(_services.Root, "review");
        _services.Files.CreateDirectory(Path.Combine(review, "cycle-2", "seats"));
        _services.Files.CreateDirectory(Path.Combine(review, "cycle-10", "seats"));
        _services.Files.CreateDirectory(Path.Combine(review, "cycle-11"));

        Assert.Equal(Path.Combine(review, "cycle-10"), _services.NotSeated.LatestCycle(review));
        Assert.Null(_services.NotSeated.LatestCycle(Path.Combine(_services.Root, "none")));
    }
}
