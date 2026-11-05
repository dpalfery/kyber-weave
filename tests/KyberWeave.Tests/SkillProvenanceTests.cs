using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Security;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every skill under <c>.apm/skills</c> must declare provenance in the nested
/// <c>metadata:</c> map the gate reads. A top-level <c>author:</c>/<c>version:</c> satisfies
/// nothing and adds <c>KW-SKILL-SPEC-009</c>, so this parses with the real engine and scans
/// with the real scanner instead of grepping the text. The check is repository-wide because
/// the fix was reverted once; a single-skill assertion would not notice.
/// </summary>
public sealed class SkillProvenanceTests
{
    private static readonly HashSet<string> ProvenanceCodes = new(StringComparer.Ordinal)
    {
        "KW-SKILL-SEC-030",
        "KW-SKILL-SEC-031",
        "KW-SKILL-SEC-032",
    };

    [Fact]
    public void EveryApmSkillDeclaresNestedProvenanceMetadata()
    {
        string skillsRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, ".apm", "skills");
        string[] skillFiles = Directory
            .EnumerateFiles(skillsRoot, "SKILL.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(skillFiles);

        SkillScanner scanner = new();
        List<string> findings = [];

        foreach (string skillFile in skillFiles)
        {
            Skill skill = SkillParser.ParseFile(skillFile);
            foreach (Diagnostic diagnostic in scanner.Scan(skill))
            {
                if (ProvenanceCodes.Contains(diagnostic.Code))
                {
                    findings.Add(
                        $"{Path.GetRelativePath(skillsRoot, skillFile)}: {diagnostic.Code} {diagnostic.Message}");
                }
            }
        }

        Assert.Empty(findings);
    }
}
