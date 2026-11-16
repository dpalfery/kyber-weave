using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Security;
using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Security;

/// <summary>
/// Scans a skill as an untrusted artifact. Per Microsoft, Anthropic and GitHub guidance,
/// a skill (SKILL.md + bundled scripts) is a trust surface to be reviewed like third-party
/// code. These are heuristics — necessary but NOT sufficient. Treat the scanner as a gate
/// that raises the bar, paired with human review and (optionally) a semantic pass.
/// </summary>
public sealed class SkillScanner
{
    public IEnumerable<Diagnostic> Scan(Skill skill)
    {
        string id = skill.Frontmatter.Name ?? skill.DirectoryName;
        string file = skill.SkillFilePath;
        InstructionSurfaceRuleCodes codes = InstructionSurfaceRuleCodes.ForSkills;

        foreach (Diagnostic d in InstructionSurfaceScanner.ScanProse(
                     skill.InstructionsBody, id, file, codes, "skill"))
            yield return d;

        foreach (SkillResource script in skill.Scripts)
        {
            string text;
            try { text = File.ReadAllText(script.AbsolutePath); }
            catch { continue; }

            foreach (Diagnostic d in InstructionSurfaceScanner.ScanScriptText(
                         text, script.RelativePath, script.AbsolutePath, id, codes))
                yield return d;
        }

        foreach (Diagnostic d in InstructionSurfaceScanner.ScanProvenance(
                     skill.Frontmatter.Metadata,
                     skill.Frontmatter.License,
                     id,
                     file,
                     codes))
            yield return d;
    }
}
