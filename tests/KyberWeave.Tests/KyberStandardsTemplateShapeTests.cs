using KyberWeave.Core.Docs.Scaffolding;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every shipped technology template is a complete folder: four files named after the
/// folder, and a standard with the six headings in order. This is the check a contributed
/// template is graded by, and it runs against the embedded copies <c>docs init</c> writes.
/// </summary>
public class KyberStandardsTemplateShapeTests
{
    private static readonly string[] SixHeadings =
    [
        "## Naming and layout",
        "## Coding standards",
        "## Testing",
        "## Commands",
        "## Security and data handling",
        "## Done checklist"
    ];

    [Fact]
    public void EveryShippedTechnologyHasItsFourFiles()
    {
        foreach (string technology in KyberStandardsTemplates.All)
        {
            foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
            {
                Assert.True(
                    KyberStandardsTemplates.TryGetPart(technology, part, out string? template),
                    $"{technology} has no template for {part}.");
                Assert.False(string.IsNullOrWhiteSpace(template));
            }
        }
    }

    [Fact]
    public void EveryStandardCarriesTheSixHeadingsInOrder()
    {
        foreach (string technology in KyberStandardsTemplates.All)
        {
            KyberStandardsTemplates.TryGetPart(
                technology, KyberStandardsTemplates.Part.Standard, out string? standard);
            string[] headings = standard!
                .Split('\n')
                .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
                .Select(line => line.TrimEnd('\r'))
                .ToArray();

            Assert.Equal(SixHeadings, headings);
        }
    }

    [Fact]
    public void EveryStandardHasATestingSection()
    {
        foreach (string technology in KyberStandardsTemplates.All)
        {
            KyberStandardsTemplates.TryGetPart(
                technology, KyberStandardsTemplates.Part.Standard, out string? standard);

            Assert.Contains("## Testing", standard, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRulesAndSkillsFileNamesItsTechnology()
    {
        foreach (string technology in KyberStandardsTemplates.All)
        {
            KyberStandardsTemplates.TryGetPart(
                technology, KyberStandardsTemplates.Part.Rules, out string? rules);
            KyberStandardsTemplates.TryGetPart(
                technology, KyberStandardsTemplates.Part.Skills, out string? skills);

            Assert.Contains($"technology: {technology}", rules, StringComparison.Ordinal);
            Assert.Contains($"technology: {technology}", skills, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnEmptyArbiterRulesFileIsShippedForEveryTechnology()
    {
        foreach (string technology in KyberStandardsTemplates.All)
        {
            KyberStandardsTemplates.TryGetPart(
                technology, KyberStandardsTemplates.Part.ArbiterRules, out string? rules);

            Assert.Contains("rules: []", rules, StringComparison.Ordinal);
        }
    }
}
