using System.Text.RegularExpressions;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Skills.Validation;

/// <summary>
/// Opinionated routing-readiness lint. Distinct from <see cref="SpecValidator"/>: spec
/// validation asks "is this a valid skill?"; the linter asks "will the orchestrator
/// route to it correctly, and is it context-efficient?".
/// </summary>
public sealed class RoutingLinter
{
    /// <summary>Recommended max body size before progressive disclosure suffers.</summary>
    public int BodyTokenBudget { get; init; } = 5000;
    public int BodyLineBudget { get; init; } = 500;

    /// <summary>Minimum description score (0-100) to pass. Used as a CI gate.</summary>
    public int MinDescriptionScore { get; init; } = 70;

    /// <summary>Cosine similarity above which two descriptions are flagged as overlapping.</summary>
    public double OverlapThreshold { get; init; } = 0.55;

    private static readonly Regex DirectiveRegex =
        new(@"\b(ALWAYS|NEVER|MUST|DO NOT|MUST NOT)\b", RegexOptions.Compiled);

    private static readonly Regex ExampleRegex =
        new(@"(^|\n)\s*(#+\s*)?(example|e\.g\.|for example|sample)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Per-skill lint (no cross-skill checks).</summary>
    public IEnumerable<Diagnostic> LintSkill(Skill skill)
    {
        var id = skill.Frontmatter.Name ?? skill.DirectoryName;
        var file = skill.SkillFilePath;

        // Description routing-readiness score
        var score = DescriptionScorer.Score(skill);
        if (score.Total < MinDescriptionScore)
        {
            var weak = score.Components.Where(c => c.Points < c.MaxPoints).Select(c => c.Name);
            yield return new Diagnostic("KW-SKILL-LINT-001", Severity.Warning,
                $"Description routing score {score.Total}/100 is below the {MinDescriptionScore} threshold. Weak dimensions: {string.Join(", ", weak)}.",
                id, file, "Run 'kyber-weave skill lint --explain' to see the full rubric.");
        }

        // Progressive-disclosure budget
        if (skill.ApproximateBodyTokens > BodyTokenBudget)
            yield return new Diagnostic("KW-SKILL-LINT-002", Severity.Warning,
                $"Body is ~{skill.ApproximateBodyTokens} tokens, over the recommended {BodyTokenBudget}. Move detail into references/ loaded on demand.",
                id, file);

        if (skill.BodyLineCount > BodyLineBudget)
            yield return new Diagnostic("KW-SKILL-LINT-003", Severity.Warning,
                $"Body is {skill.BodyLineCount} lines, over the recommended {BodyLineBudget}. Consider splitting into reference files.",
                id, file);

        // Body directive presence
        if (!DirectiveRegex.IsMatch(skill.InstructionsBody))
            yield return new Diagnostic("KW-SKILL-LINT-004", Severity.Info,
                "Body has no explicit ALWAYS/NEVER/MUST directives. Firm directives help the agent follow the skill consistently.",
                id, file);

        // At least one worked example
        if (!ExampleRegex.IsMatch(skill.InstructionsBody))
            yield return new Diagnostic("KW-SKILL-LINT-005", Severity.Info,
                "Body contains no worked example. A concrete example improves how reliably the agent applies the skill.",
                id, file);

        // Empty body
        if (string.IsNullOrWhiteSpace(skill.InstructionsBody))
            yield return new Diagnostic("KW-SKILL-LINT-006", Severity.Warning,
                "Skill has an empty instruction body. The agent has nothing to act on once the skill loads.",
                id, file);
    }

    /// <summary>Cross-skill checks over a set: name collisions and description overlap.</summary>
    public IEnumerable<Diagnostic> LintSet(SkillSet set)
    {
        var skills = set.Skills;

        // Exact name collisions
        var byName = skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Frontmatter.Name))
            .GroupBy(s => s.Frontmatter.Name!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var group in byName)
        {
            yield return new Diagnostic("KW-SKILL-LINT-010", Severity.Error,
                $"{group.Count()} skills share the name '{group.Key}'. Names must be unique so the orchestrator can disambiguate.",
                group.Key, string.Join(", ", group.Select(s => s.SkillFilePath)));
        }

        // Description overlap (near-duplicate routing targets)
        var vectors = skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Frontmatter.Description))
            .Select(s => (Skill: s, Vec: TextVectorizer.Vectorize(s.Frontmatter.Description!)))
            .ToList();

        for (int i = 0; i < vectors.Count; i++)
        {
            for (int j = i + 1; j < vectors.Count; j++)
            {
                var sim = TextVectorizer.CosineSimilarity(vectors[i].Vec, vectors[j].Vec);
                if (sim >= OverlapThreshold)
                {
                    var a = vectors[i].Skill.Frontmatter.Name ?? vectors[i].Skill.DirectoryName;
                    var b = vectors[j].Skill.Frontmatter.Name ?? vectors[j].Skill.DirectoryName;
                    yield return new Diagnostic("KW-SKILL-LINT-011", Severity.Warning,
                        $"Descriptions of '{a}' and '{b}' overlap ({sim:P0} lexical similarity). The orchestrator may route ambiguously between them.",
                        $"{a} / {b}", vectors[j].Skill.SkillFilePath,
                        "Sharpen each description's boundary, or merge the skills.");
                }
            }
        }
    }
}
