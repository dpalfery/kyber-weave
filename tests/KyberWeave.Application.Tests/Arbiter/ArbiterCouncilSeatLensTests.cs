using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A review-lens dispatch is a council seat for every lens in the review lens catalogue, so its reply is saved
/// as a seat file. A lens the header grammar did not know classified as an ordinary investigation.
/// </summary>
public sealed class ArbiterCouncilSeatLensTests
{
    public static IEnumerable<object[]> CatalogueLenses() =>
        ReviewLensCatalogue.Names.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(CatalogueLenses))]
    public void Classify_ReviewLensDispatchWithAnyCatalogueLens_IsLensSpawn(string lens)
    {
        ArbiterEvent ev = new()
        {
            Harness = "copilot-cli",
            Phase = "pre",
            Target = "review-lens",
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
            Prompt = $"KYBER-ARBITER: true\nLENS: {lens}\nREVIEW_RUN: run-cycle-2\n\nPacket: /tmp/packets/{lens}.md",
        };

        TriggerClassification row = Assert.Single(TriggerClassifier.Classify(ev));

        Assert.Equal("lens.spawn", row.Trigger);
        Assert.Equal(lens, row.Headers["LENS"]);
    }
}
