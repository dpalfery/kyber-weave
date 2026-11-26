using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The savings keys in <c>docs/configuration.md</c> name the same profiles and developer-tier table the code ships.
/// A profile or default added on one side and not the other would document a key the loader refuses or a profile the
/// Arbiter cannot route to.
/// </summary>
public sealed class ConfigurationModelTargetingDocsTests
{
    private const string Document = "docs/configuration.md";

    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly string[] Difficulties = ["easy", "standard", "hard"];

    [Fact]
    public void DocumentedSemanticProfileListMatchesArbiterSemanticProfiles()
    {
        string prose = ParagraphContaining(Corpus.ReadText(Document), "A semantic profile is one of");

        Assert.Equal(ArbiterSemanticProfiles.All, Backticked(prose));
    }

    [Fact]
    public void DocumentedDifficultyTableAndLadderMatchTierProfiles()
    {
        string[] lines = Lines();

        string[] difficulty = Cells(Row(lines, "| `model-targeting.difficulty-profiles.easy`"));
        string expectedDefaults = string.Join(" / ", Difficulties
            .Select(difficulty => $"`{TierProfiles.Default.DifficultyProfiles[difficulty]}`"));
        Assert.Equal(expectedDefaults, difficulty[2]);

        string[] ladder = Cells(Row(lines, "| `model-targeting.ladder`"));
        Assert.Equal($"[{string.Join(", ", TierProfiles.Default.Ladder)}]", ladder[2].Trim('`'));
    }

    [Fact]
    public void DocumentedYamlExampleMatchesTierProfiles()
    {
        string[] lines = Lines();
        int start = Array.FindIndex(lines, line => line.Trim() == "difficulty-profiles:");
        Assert.True(start >= 0, "The savings keys example must show difficulty-profiles.");

        for (int index = 0; index < Difficulties.Length; index++)
        {
            string difficulty = Difficulties[index];
            Assert.Equal(
                $"{difficulty}: {TierProfiles.Default.DifficultyProfiles[difficulty]}",
                lines[start + 1 + index].Trim());
        }

        Assert.Equal(
            $"ladder: [{string.Join(", ", TierProfiles.Default.Ladder)}]",
            lines[start + 1 + Difficulties.Length].Trim());
    }

    private static string[] Lines() => Corpus.ReadText(Document).Split('\n');

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
