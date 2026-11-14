using System.Text.RegularExpressions;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The council's lens list lives in two places: the code the verdict command checks seats against, and the
/// lens table in the code-review skill that tells the orchestrator which seats to run. A seat the skill names
/// but the catalogue does not, or the reverse, would be refused or silently unaccounted, so the two must agree.
/// </summary>
public sealed class ReviewLensCatalogueTests
{
    private const int ExpectedLensCount = 17;

    /// <summary>
    /// A lens row in the skill links its reference file, as <c>[name](references/lenses/name.md)</c>. Matching on the
    /// link keeps prose that happens to name a lens out of the count.
    /// </summary>
    private static readonly Regex LensRow = new(
        @"^\|\s*\[(?<name>[a-z0-9-]+)\]\(lenses/\k<name>\.md\)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void TheCatalogueNamesTheLensesTheCodeReviewSkillLists()
    {
        string skill = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "products",
            "kyber-squad",
            "skills",
            "code-review",
            "references",
            "first-review.md"));

        string[] listed = LensRow.Matches(skill)
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedLensCount, listed.Length);
        Assert.Equal(listed, ReviewLensCatalogue.Names.Order(StringComparer.Ordinal));
    }
}
