using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the test developer's Arbiter contract (packet 7.4): the completion digest carries a
/// machine-readable <c>RED_EVIDENCE:</c> line the in-flight ledger can parse for the
/// <c>KW-ARB-MODE-001</c> fact <c>ledger.red-evidence</c> — the runner filter, the failing
/// tests and the reason, or <c>none</c> when the work recorded no RED run.
/// </summary>
/// <remarks>
/// The digest template is a report format, and report formats belong to the one skill a worker
/// loads when it finishes: slices of this programme moved them out of each agent body so the same
/// template is not maintained once per agent (the rule map's "Report formats" row). This class
/// pins the line's tokens and its place in the template; the prose around it (the ledger parses
/// it, <c>none</c> means no RED run) is catalog rule A2 in
/// <c>tests/KyberWeave.Tests/Fixtures/prose-rules/catalog.yml</c>.
/// </remarks>
public sealed class ArbiterTestDevContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string TaskGateContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "skills", "task-gate", "SKILL.md"));

    [Fact]
    public void CompletionDigest_CarriesTheMachineReadableRedEvidenceLine()
    {
        string contract = TaskGateContract;

        Assert.Contains(
            "RED_EVIDENCE: <runner filter> — <failing tests> — <reason>",
            contract,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionDigest_RecordsNoneWhenTheWorkRecordedNoRedRun()
    {
        string contract = TaskGateContract;

        Assert.Contains("RED_EVIDENCE: none", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void RedEvidenceLine_IsPartOfTheCompletionDigestTemplate()
    {
        string contract = TaskGateContract;

        int digestStart = contract.IndexOf("## Completion digest", StringComparison.Ordinal);
        Assert.True(digestStart >= 0, "the task-gate skill must keep its Completion digest section.");
        string digest = contract[digestStart..];

        Assert.Contains(
            "RED_EVIDENCE: <runner filter> — <failing tests> — <reason>",
            digest,
            StringComparison.Ordinal);
        Assert.Contains("RED_EVIDENCE: none", digest, StringComparison.Ordinal);
        // The line is a digest field, not prose: it belongs in the returned template.
        int templateStart = digest.IndexOf("STATUS: PASS | FAIL | ESCALATION", StringComparison.Ordinal);
        Assert.True(templateStart >= 0, "the completion digest template must stay pinned.");
        Assert.Contains("RED_EVIDENCE:", digest[templateStart..], StringComparison.Ordinal);
    }
}
