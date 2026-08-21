using System.Text.RegularExpressions;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Validation;

/// <summary>
/// Validates a skill against the Agent Skills open-format specification
/// (https://agentskills.io/specification). These are conformance rules: an Error means
/// the skill is likely to misbehave or silently fail in a compliant runtime.
/// </summary>
public static partial class SpecValidator
{
    public const int NameMaxLength = 64;
    public const int DescriptionMaxLength = 1024;
    public const int CompatibilityMaxLength = 500;

    // lowercase letters, digits, single hyphens; no leading/trailing/consecutive hyphens
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NamePattern();

    public static IEnumerable<Diagnostic> Validate(Skill skill)
    {
        string? name = skill.Frontmatter.Name;
        string skillId = string.IsNullOrWhiteSpace(name) ? skill.DirectoryName : name;
        string file = skill.SkillFilePath;

        // ---- name ----
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return new Diagnostic("KW-SKILL-SPEC-001", Severity.Error,
                "Front matter is missing the required 'name' field.", skillId, file,
                "Add a 'name:' that matches the skill's directory name.");
        }
        else
        {
            if (name.Length > NameMaxLength)
                yield return new Diagnostic("KW-SKILL-SPEC-002", Severity.Error,
                    $"'name' is {name.Length} chars; the spec limit is {NameMaxLength}.", skillId, file);

            if (!NamePattern().IsMatch(name))
                yield return new Diagnostic("KW-SKILL-SPEC-003", Severity.Error,
                    $"'name' \"{name}\" must be lowercase letters, digits and single hyphens (no leading/trailing/consecutive hyphens).",
                    skillId, file, "e.g. 'hr-leave-eligibility-triage'.");

            // name must match parent directory — the classic silent-load failure
            if (Directory.Exists(skill.DirectoryPath) &&
                !string.Equals(name, skill.DirectoryName, StringComparison.Ordinal))
            {
                yield return new Diagnostic("KW-SKILL-SPEC-004", Severity.Error,
                    $"'name' (\"{name}\") does not match the directory name (\"{skill.DirectoryName}\"). Many runtimes will fail to load the skill silently.",
                    skillId, file, $"Rename the folder to \"{name}\" or change 'name' to \"{skill.DirectoryName}\".");
            }
        }

        // ---- description ----
        string? description = skill.Frontmatter.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            yield return new Diagnostic("KW-SKILL-SPEC-005", Severity.Error,
                "Front matter is missing the required 'description' field. This is the routing signal the orchestrator uses to select the skill.",
                skillId, file);
        }
        else if (description.Length > DescriptionMaxLength)
        {
            yield return new Diagnostic("KW-SKILL-SPEC-006", Severity.Error,
                $"'description' is {description.Length} chars; the spec limit is {DescriptionMaxLength}.", skillId, file);
        }

        // ---- compatibility ----
        if (!string.IsNullOrEmpty(skill.Frontmatter.Compatibility) &&
            skill.Frontmatter.Compatibility!.Length > CompatibilityMaxLength)
        {
            yield return new Diagnostic("KW-SKILL-SPEC-007", Severity.Warning,
                $"'compatibility' is {skill.Frontmatter.Compatibility.Length} chars; the spec recommends a max of {CompatibilityMaxLength}.",
                skillId, file);
        }

        // ---- angle brackets in front matter (system-prompt injection safety note) ----
        if (skill.RawFrontmatter.Contains('<') || skill.RawFrontmatter.Contains('>'))
        {
            yield return new Diagnostic("KW-SKILL-SPEC-008", Severity.Error,
                "Front matter contains '<' or '>'. The spec advises avoiding angle brackets in front matter because they can inject content into the system prompt.",
                skillId, file);
        }

        // ---- unknown keys ----
        foreach (string unknown in skill.Frontmatter.UnknownKeys.Keys)
        {
            yield return new Diagnostic("KW-SKILL-SPEC-009", Severity.Warning,
                $"Front matter contains unrecognized key '{unknown}'. Compliant runtimes ignore it; confirm it is intentional.",
                skillId, file);
        }

        // ---- allowed-tools is experimental ----
        if (!string.IsNullOrWhiteSpace(skill.Frontmatter.AllowedToolsRaw))
        {
            yield return new Diagnostic("KW-SKILL-SPEC-010", Severity.Info,
                "'allowed-tools' is experimental and is NOT a security control — runtimes are not required to enforce it. Do not rely on it for isolation.",
                skillId, file);
        }

        // ---- file reference integrity ----
        foreach (SkillReferenceLink link in skill.ReferenceLinks)
        {
            if (link.Target.Contains(".."))
            {
                yield return new Diagnostic("KW-SKILL-SPEC-011", Severity.Error,
                    $"Body references '{link.Target}', which escapes the skill directory ('..'). Path traversal is not allowed.",
                    skillId, file);
            }
            else if (!link.Resolves)
            {
                yield return new Diagnostic("KW-SKILL-SPEC-012", Severity.Error,
                    $"Body references '{link.Target}', but no such file or directory exists in the skill bundle.",
                    skillId, file, "Add the referenced file, or fix the path.");
            }
        }
    }
}
