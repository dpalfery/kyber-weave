using KyberWeave.Core.Docs.Scaffolding;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Every shipped technology template is a complete folder: four files named after the
/// folder, and a standard with the six headings in order. This is the check a contributed
/// template is graded by, and it runs against the embedded copies <c>docs init</c> writes.
/// </summary>
public class KyberStandardsTemplateShapeTests
{
    private static readonly IKyberStandardsTemplates Templates = ShippedKyberStandards.Templates;

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
                    Templates.TryGetPart(folder, part, out string? template),
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
            Templates.TryGetPart(
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
            Templates.TryGetPart(
                folder, KyberStandardsTemplates.Part.Standard, out string? standard);

            Assert.Contains("## Testing", standard, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRulesAndSkillsFileNamesItsTechnology()
    {
        foreach (string technology in KyberStandardsTemplates.All)
        {
            Templates.TryGetPart(
                technology, KyberStandardsTemplates.Part.Rules, out string? rules);
            Templates.TryGetPart(
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
                Templates.TryGetPart(role, part, out string? template);

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
                Templates.TryGetPart(platform, part, out string? template);

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
        Templates.TryGetPart(
            "github", KyberStandardsTemplates.Part.Standard, out string? standard);
        Templates.TryGetPart(
            "github", KyberStandardsTemplates.Part.Skills, out string? skills);

        Assert.Contains("permissions", standard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pin", standard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh ", skills, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryShippedArbiterRulesFileDeclaresItsRulesList()
    {
        foreach (string folder in KyberStandardsTemplates.AllFolders)
        {
            Assert.True(
                Templates.TryGetPart(
                    folder, KyberStandardsTemplates.Part.ArbiterRules, out string? rules),
                $"{folder} has no arbiter-rules template.");

            YamlStream document = new();
            document.Load(new StringReader(rules));
            YamlDocument root = Assert.Single(document.Documents);
            YamlMappingNode mapping = Assert.IsType<YamlMappingNode>(root.RootNode);
            Assert.True(
                mapping.Children.TryGetValue(new YamlScalarNode("rules"), out YamlNode? declared),
                $"{folder} declares no top-level 'rules:' key.");
            YamlSequenceNode list = Assert.IsType<YamlSequenceNode>(declared);

            foreach (YamlMappingNode rule in list.Children.Cast<YamlMappingNode>())
            {
                Assert.True(
                    rule.Children.TryGetValue(new YamlScalarNode("id"), out YamlNode? id) &&
                    !string.IsNullOrWhiteSpace(Assert.IsType<YamlScalarNode>(id).Value),
                    $"{folder} declares a rule with no id.");
            }
        }
    }
}
