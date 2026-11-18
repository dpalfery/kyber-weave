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
    /// <summary>A bundled script that could not be read, so it was not scanned (KW-SKILL-SEC-014).</summary>
    public const string RuleUnreadableScript = "KW-SKILL-SEC-014";

    /// <summary>Scans the skill's prose, its bundled scripts and its provenance.</summary>
    /// <param name="skill">The skill to scan.</param>
    /// <param name="readScriptText">Reads one bundled script; returning null or throwing is reported as a finding, because a script nobody could read was not scanned.</param>
    /// <returns>One diagnostic per finding, in prose, script and provenance order.</returns>
    public IEnumerable<Diagnostic> Scan(Skill skill, Func<SkillResource, string?> readScriptText)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(readScriptText);

        return ScanCore(skill, readScriptText);
    }

    private static IEnumerable<Diagnostic> ScanCore(Skill skill, Func<SkillResource, string?> readScriptText)
    {
        string id = skill.Frontmatter.Name ?? skill.DirectoryName;
        string file = skill.SkillFilePath;
        InstructionSurfaceRuleCodes codes = InstructionSurfaceRuleCodes.ForSkills;

        // A skill with no parsed body has no prose to scan; spec validation reports the missing body.
        foreach (Diagnostic d in InstructionSurfaceScanner.ScanProse(
                     skill.InstructionsBody ?? string.Empty, id, file, codes, "skill"))
            yield return d;

        foreach (SkillResource script in skill.Scripts)
        {
            string? text;
            try { text = readScriptText(script); }
            catch { text = null; }

            if (text is null)
            {
                yield return new Diagnostic(RuleUnreadableScript, Severity.Warning,
                    $"Bundled script '{script.RelativePath}' could not be read, so it was not scanned. "
                    + "An unscanned script can carry any command; make it readable and scan again.",
                    id, script.AbsolutePath);
                continue;
            }

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
