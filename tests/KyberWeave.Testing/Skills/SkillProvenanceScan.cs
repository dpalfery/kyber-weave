using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Security;
using Xunit;

namespace KyberWeave.Tests.Skills;

/// <summary>
/// Scans a set of skills for provenance problems the way the gate does: it parses each one with the engine and
/// scans it with the scanner, instead of grepping the text.
/// </summary>
/// <remarks>
/// One broken skill is reported as a finding, not thrown, so it cannot hide every skill after it from the scan. A
/// top-level <c>author:</c> or <c>version:</c> satisfies nothing, so an unknown top-level key is a finding too.
/// </remarks>
public static class SkillProvenanceScan
{
    private static readonly HashSet<string> ProvenanceCodes = new(StringComparer.Ordinal)
    {
        "KW-SKILL-SEC-030",
        "KW-SKILL-SEC-031",
        "KW-SKILL-SEC-032",
    };

    /// <summary>Scans the skills and lists every provenance problem.</summary>
    /// <param name="skillsRoot">The directory the reported names are relative to.</param>
    /// <param name="skillFiles">The SKILL.md paths to scan, in the order to report them.</param>
    /// <param name="parse">Reads and parses one SKILL.md.</param>
    /// <param name="readScript">Reads the text of a script a skill carries.</param>
    /// <returns>One line per problem; empty when every skill declares nested provenance.</returns>
    public static IReadOnlyList<string> Issues(
        string skillsRoot,
        IEnumerable<string> skillFiles,
        Func<string, Skill> parse,
        Func<string, string> readScript)
    {
        ArgumentNullException.ThrowIfNull(skillFiles);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(readScript);
        string[] ordered = [.. skillFiles.Order(StringComparer.Ordinal)];

        Assert.NotEmpty(ordered);

        SkillScanner scanner = new();
        List<string> issues = [];

        foreach (string skillFile in ordered)
        {
            string relative = Path.GetRelativePath(skillsRoot, skillFile);
            Skill skill;
            try
            {
                skill = parse(skillFile);
            }
            catch (SkillParseException exception)
            {
                issues.Add($"{relative}: cannot parse front matter: {exception.Message}");
                continue;
            }
            catch (IOException exception)
            {
                issues.Add($"{relative}: cannot read skill: {exception.Message}");
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                issues.Add($"{relative}: cannot read skill: {exception.Message}");
                continue;
            }

            foreach (Diagnostic diagnostic in scanner.Scan(skill, script => readScript(script.AbsolutePath)))
            {
                if (ProvenanceCodes.Contains(diagnostic.Code))
                {
                    issues.Add($"{relative}: {diagnostic.Code} {diagnostic.Message}");
                }
            }

            foreach (string key in skill.Frontmatter.UnknownKeys.Keys)
            {
                issues.Add($"{relative}: unexpected top-level front matter key '{key}'");
            }
        }

        return issues;
    }
}
