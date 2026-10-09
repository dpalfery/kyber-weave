using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the test developer's Arbiter contract (packet 7.4): the completion digest
/// carries a machine-readable <c>RED_EVIDENCE:</c> line the in-flight ledger can
/// parse for the <c>KW-ARB-MODE-001</c> fact <c>ledger.red-evidence</c> — the
/// runner filter, the failing tests and the reason, or <c>none</c> when the work
/// recorded no RED run.
/// </summary>
public sealed class ArbiterTestDevContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string TestDevContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "agents", "test-dev.md"));

    [Fact]
    public void CompletionDigest_CarriesTheMachineReadableRedEvidenceLine()
    {
        string contract = TestDevContract;

        Assert.Contains(
            "RED_EVIDENCE: <runner filter> — <failing tests> — <reason>",
            contract,
            StringComparison.Ordinal);
        Assert.Contains("in-flight ledger", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionDigest_RecordsNoneWhenTheWorkRecordedNoRedRun()
    {
        string contract = TestDevContract;

        Assert.Contains("RED_EVIDENCE: none", contract, StringComparison.Ordinal);
        Assert.Contains("no RED run", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void RedEvidenceLine_IsPartOfTheCompletionDigestTemplate()
    {
        string contract = TestDevContract;

        int digestStart = contract.IndexOf("## Completion digest", StringComparison.Ordinal);
        Assert.True(digestStart >= 0, "test-dev.md must keep its Completion digest section.");
        string digest = contract[digestStart..];

        Assert.Contains(
            "RED_EVIDENCE: <runner filter> — <failing tests> — <reason>",
            digest,
            StringComparison.Ordinal);
        Assert.Contains("RED_EVIDENCE: none", digest, StringComparison.Ordinal);
        // The line is a digest field, not prose: it belongs in the returned template.
        int templateStart = digest.IndexOf("STATUS: READY_FOR_REVIEW", StringComparison.Ordinal);
        Assert.True(templateStart >= 0, "the completion digest template must stay pinned.");
        Assert.Contains("RED_EVIDENCE:", digest[templateStart..], StringComparison.Ordinal);
    }
}
