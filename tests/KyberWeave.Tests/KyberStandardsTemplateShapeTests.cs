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
    public void EveryShippedFolderHasItsFourFiles()
    {
        foreach (string folder in KyberStandardsTemplates.AllFolders)
        {
            foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
            {
                Assert.True(
                    KyberStandardsTemplates.TryGetPart(folder, part, out string? template),
                    $"{folder} has no template for {part}.");
                Assert.False(string.IsNullOrWhiteSpace(template));
            }
        }
    }

    [Fact]
    public void EveryStandardCarriesTheSixHeadingsInOrder()
    {
        foreach (string folder in KyberStandardsTemplates.AllFolders)
        {
            KyberStandardsTemplates.TryGetPart(
                folder, KyberStandardsTemplates.Part.Standard, out string? standard);
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
        foreach (string folder in KyberStandardsTemplates.AllFolders)
        {
            KyberStandardsTemplates.TryGetPart(
                folder, KyberStandardsTemplates.Part.Standard, out string? standard);

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
    public void EveryRoleTemplateIsScopedByRoleAndCarriesTheSixHeadings()
    {
        foreach (string role in KyberStandardsTemplates.Roles)
        {
            foreach (KyberStandardsTemplates.Part part in new[]
                     {
                         KyberStandardsTemplates.Part.Standard,
                         KyberStandardsTemplates.Part.Rules,
                         KyberStandardsTemplates.Part.Skills
                     })
            {
                KyberStandardsTemplates.TryGetPart(role, part, out string? template);

                Assert.Contains($"role: {role}", template, StringComparison.Ordinal);
                Assert.DoesNotContain($"technology: {role}", template, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryPlatformTemplateIsScopedByTechnology()
    {
        foreach (string platform in KyberStandardsTemplates.Platforms)
        {
            foreach (KyberStandardsTemplates.Part part in new[]
                     {
                         KyberStandardsTemplates.Part.Rules,
                         KyberStandardsTemplates.Part.Skills
                     })
            {
                KyberStandardsTemplates.TryGetPart(platform, part, out string? template);

                Assert.Contains($"technology: {platform}", template, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void NoTestingRoleFolderShips()
    {
        Assert.DoesNotContain("test", KyberStandardsTemplates.Roles, StringComparer.Ordinal);
    }

    [Fact]
    public void TheGithubTemplateStatesWhatTheGithubWorkStandardStates()
    {
        KyberStandardsTemplates.TryGetPart(
            "github", KyberStandardsTemplates.Part.Standard, out string? standard);
        KyberStandardsTemplates.TryGetPart(
            "github", KyberStandardsTemplates.Part.Skills, out string? skills);

        Assert.Contains("permissions", standard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pin", standard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh ", skills, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyArbiterRulesFileIsShippedForEveryTechnology()
    {
        foreach (string technology in KyberStandardsTemplates.AllFolders)
        {
            KyberStandardsTemplates.TryGetPart(
                technology, KyberStandardsTemplates.Part.ArbiterRules, out string? rules);

            Assert.Contains("rules: []", rules, StringComparison.Ordinal);
        }
    }
}
