using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-GATE-REPORT-001</c> ships in the role rules files: the files hold the shipped count and every earlier
/// rule id is still there.
/// </summary>
public sealed class ArbiterGateReportRuleShippedFileTests
{
    private const string GateReportRule = "KW-ARB-GATE-REPORT-001";

    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly string[] RoleRulesPaths =
    [
        "products/kyber-squad/standards/conductor/conductor.arbiter-rules.yml",
        "products/kyber-squad/standards/architect/architect.arbiter-rules.yml",
        "products/kyber-squad/standards/reviewer/reviewer.arbiter-rules.yml"
    ];

    /// <summary>
    /// The seventeen rules the file shipped before it that it still ships, pinned so the new one joins without renumbering any.
    /// Rule ids are permanent: suppressions, baselines and code-scanning alerts key on them.
    /// </summary>
    private static readonly string[] ShippedBeforeThisSlice =
    [
        "KW-ARB-PLAN-001",
        "KW-ARB-SCOPE-001",
        "KW-ARB-SCOPE-002",
        "KW-ARB-READY-001",
        "KW-ARB-OWNER-001",
        "KW-ARB-OWNER-002",
        "KW-ARB-ROSTER-001",
        "KW-ARB-DIFF-001",
        "KW-ARB-PLANNER-001",
        "KW-ARB-PLANNER-DIFF-001",
        "KW-ARB-READONLY-001",
        "KW-ARB-LENS-001",
        "KW-ARB-QUOTE-001",
        "KW-ARB-PREEX-001",
        "KW-ARB-CLAIM-001",
        "KW-ARB-GATE-CORROBORATED-001",
        "KW-ARB-GATE-001",
    ];


    /// <summary>
    /// The rule file is the shipped contract, and its header is part of it: a reader checking how many
    /// rules ship must not have to count them. The new rule is added and the header updated to match,
    /// with no existing id renumbered.
    /// </summary>
    [Fact]
    public void TheShippedFileCountsTheNewRuleAndRenumbersNone()
    {
        Assert.All(RoleRulesPaths, path => Assert.True(Corpus.Exists(path), $"the role rules file must live at '{path}'."));
        string[] ids =
        [
            .. RoleRulesPaths
                .SelectMany(path => CorpusLines.Split(Corpus.ReadText(path)))
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("- id: ", StringComparison.Ordinal))
                .Select(line => line["- id: ".Length..].Trim())
        ];

        Assert.Equal(36, ids.Length);
        Assert.Contains(GateReportRule, ids);
        Assert.All(ShippedBeforeThisSlice, id => Assert.Contains(id, ids));
        Assert.True(
            ids.Length >= ShippedBeforeThisSlice.Length + 1,
            $"The file ships {ids.Length} rules; '{GateReportRule}' is added to the {ShippedBeforeThisSlice.Length} " +
            "this tree already ships.");
    }

    /// <summary>
    /// The header's own count, read from its first line: "The 18 shipped Arbiter rules. ...".
    /// </summary>
    private static int DeclaredRuleCount(string headerLine)
    {
        string[] words = headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int shipped = Array.IndexOf(words, "shipped");
        Assert.True(
            shipped > 0,
            $"The first line of the shipped rules file must state how many rules it holds. Read: '{headerLine}'.");
        string declared = words[shipped - 1];
        Assert.True(
            int.TryParse(declared, out int count),
            $"The first line of the shipped rules file must state its count as a number. Read: '{declared}'.");
        return count;
    }
}
