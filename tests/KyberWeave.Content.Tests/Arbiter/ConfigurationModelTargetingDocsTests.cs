using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The configuration, architecture and rule-reference pages name the same profiles and developer-tier
/// table the code ships, and say both host overrides are refused. A profile or default added on one
/// side and not the other would document a key the loader refuses or a profile the Arbiter cannot
/// route to.
/// </summary>
public sealed class ConfigurationModelTargetingDocsTests
{
    private const string ConfigurationDocument = "docs/configuration.md";

    private const string ArchitectureDocument = "docs/kyber-arbiter/architecture.md";

    private const string RuleReferenceDocument = "docs/ci-pipelines/rule-reference.md";

    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly string[] Difficulties = ["easy", "standard", "hard"];

    [Fact]
    public void DocumentedSemanticProfileListMatchesArbiterSemanticProfiles()
    {
        string prose = ParagraphContaining(Corpus.ReadText(ConfigurationDocument), "A semantic profile is one of");

        Assert.Equal(ArbiterSemanticProfiles.All, Backticked(prose));
    }

    [Fact]
    public void DocumentedDifficultyTableAndLadderMatchTierProfiles()
    {
        string[] lines = Lines(ConfigurationDocument);

        string[] difficulty = Cells(Row(lines, "| `model-targeting.difficulty-profiles.easy`"));
        string expectedDefaults = string.Join(" / ", Difficulties
            .Select(difficulty => $"`{TierProfiles.Default.DifficultyProfiles[difficulty]}`"));
        Assert.Equal(expectedDefaults, difficulty[2]);

        string[] ladder = Cells(Row(lines, "| `model-targeting.ladder`"));
        Assert.Equal($"[{string.Join(", ", TierProfiles.Default.Ladder)}]", ladder[2].Trim('`'));
    }

    [Fact]
    public void BothModelTargetingKeyRowsAreRefusedWithConfig012()
    {
        string[] lines = Lines(ConfigurationDocument);

        Assert.Equal("`KW-ARB-CONFIG-012`", Cells(Row(lines, "| `model-targeting.difficulty-profiles.easy`"))[4]);
        Assert.Equal("`KW-ARB-CONFIG-012`", Cells(Row(lines, "| `model-targeting.ladder`"))[4]);
    }

    [Fact]
    public void SavingsKeyExampleLeavesOutTheRefusedTableAndLadder()
    {
        string[] lines = Lines(ConfigurationDocument);

        Assert.DoesNotContain(lines, line => line.Trim() == "difficulty-profiles:");
        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("ladder:", StringComparison.Ordinal));
    }

    [Fact]
    public void ArchitectureStatesTheShippedTableAndRefusesBothHostKeys()
    {
        string prose = ParagraphContaining(Corpus.ReadText(ArchitectureDocument), "the shipped table maps");

        Assert.Contains(ShippedTable(), prose, StringComparison.Ordinal);
        Assert.Contains($"the shipped ladder is {ShippedLadder()}", prose, StringComparison.Ordinal);
        Assert.Contains("`model-targeting.difficulty-profiles`", prose, StringComparison.Ordinal);
        Assert.Contains("`model-targeting.ladder`", prose, StringComparison.Ordinal);
        Assert.Contains("refused", prose, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleReferenceConfig012RowStatesTheShippedTableAndBothRefusedKeys()
    {
        string row = Row(Lines(RuleReferenceDocument), "| `KW-ARB-CONFIG-012`");

        Assert.Contains(ShippedTable(), row, StringComparison.Ordinal);
        Assert.Contains(ShippedLadder(), row, StringComparison.Ordinal);
        Assert.Contains("`model-targeting.difficulty-profiles`", row, StringComparison.Ordinal);
        Assert.Contains("`model-targeting.ladder`", row, StringComparison.Ordinal);
        Assert.Contains("refused", row, StringComparison.Ordinal);
    }

    private static string ShippedTable() => string.Join(", ", Difficulties
        .Select(difficulty => $"`{difficulty}` to `{TierProfiles.Default.DifficultyProfiles[difficulty]}`"));

    private static string ShippedLadder() =>
        $"`{string.Join("`, `", TierProfiles.Default.Ladder)}`";

    private static string[] Lines(string document) => Corpus.ReadText(document).Split('\n');

    private static string Row(string[] lines, string prefix) =>
        Assert.Single(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static string[] Cells(string row) => [.. row.Split('|').Select(cell => cell.Trim())];

    private static string ParagraphContaining(string text, string phrase)
    {
        string[] lines = text.Split('\n');
        int start = Array.FindIndex(lines, line => line.Contains(phrase, StringComparison.Ordinal));
        Assert.True(start >= 0, $"The document must state '{phrase}'.");

        int end = start;
        while (end < lines.Length && !string.IsNullOrWhiteSpace(lines[end]))
            end++;

        return string.Join(' ', lines[start..end]);
    }

    private static string[] Backticked(string text)
    {
        string[] parts = text.Split('`');
        return [.. parts.Where((_, index) => index % 2 == 1)];
    }
}
