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
public sealed partial class RoutingLinter
{
    /// <summary>Recommended max body size before progressive disclosure suffers.</summary>
    public int BodyTokenBudget { get; init; } = 5000;
    public int BodyLineBudget { get; init; } = 500;

    /// <summary>Minimum description score (0-100) to pass. Used as a CI gate.</summary>
    public int MinDescriptionScore { get; init; } = 70;

    /// <summary>Cosine similarity above which two descriptions are flagged as overlapping.</summary>
    public double OverlapThreshold { get; init; } = 0.55;

    [GeneratedRegex(@"\b(ALWAYS|NEVER|MUST|DO NOT|MUST NOT)\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"(^|\n)\s*(#+\s*)?(example|e\.g\.|for example|sample)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ExampleRegex();

    [GeneratedRegex(@"\b((use|uses)\s+(this\s+)?(skill\s+|agent\s+|tool\s+)?(when|for)|(apply|applies|invoke|invokes|trigger|triggers)\s+when)\b",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TriggerClauseRegex();

    [GeneratedRegex(@"^\s*(generat|creat|validat|triag|classif|rout|calculat|comput|look\s*up|retriev|summari|draft|review|extract|analy|check|process|handl|resolv|onboard|document|format|convert|pars|fetch|quer|escalat|approv|reset|enrich|map|build|execut|perform|manag|sync|scan|transform|updat|deploy|evaluat|track|monitor|filter|publish|writ|read|provid|assist|help|enabl|implement|automat|integrat|orchestrat|compil|modif|inspect|run|send|collect|aggregat)\w*\b",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ActionSummaryRegex();

    [GeneratedRegex(@"\b(" +
        @"this\s+(skill|tool|agent)\s+(is\s+)?(designed\s+to|intended\s+to|can\s+be\s+used\s+by|allows?|helps?|aims\s+to|provides?)|" +
        @"the\s+purpose\s+of\s+this\s+(skill|tool|agent)\s+is\s+to|" +
        @"in\s+this\s+(skill|tool|agent)\s+(we\s+)?(provide|offer|give)|" +
        @"(is\s+)?designed\s+to|" +
        @"(is\s+)?intended\s+to|" +
        @"(is\s+)?capable\s+of|" +
        @"serves\s+as\s+a(n)?|" +
        @"in\s+order\s+to|" +
        @"allows?\s+(the\s+)?(user|agent)s?\s+to|" +
        @"functionality\s+to|" +
        @"can\s+be\s+used\s+by\s+(the\s+)?(user|agent)" +
        @")\b",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FillerRegex();

    /// <summary>Per-skill lint (no cross-skill checks).</summary>
    public IEnumerable<Diagnostic> LintSkill(Skill skill)
    {
        string id = skill.Frontmatter.Name ?? skill.DirectoryName;
        string file = skill.SkillFilePath;

        // Description routing-readiness score
        DescriptionScore score = DescriptionScorer.Score(skill);
        if (score.Total < MinDescriptionScore)
        {
            IEnumerable<string> weak = score.Components.Where(c => c.Points < c.MaxPoints).Select(c => c.Name);
            yield return new Diagnostic("KW-SKILL-LINT-001", Severity.Warning,
                $"Description routing score {score.Total}/100 is below the {MinDescriptionScore} threshold. Weak dimensions: {string.Join(", ", weak)}.",
                id, file, "Run 'kyber-weave skill lint --explain' to see the full rubric.");
        }

        // Action summary without trigger framing
        string description = (skill.Frontmatter.Description ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(description))
        {
            if (!TriggerClauseRegex().IsMatch(description) && ActionSummaryRegex().IsMatch(description))
            {
                yield return new Diagnostic("KW-SKILL-LINT-007", Severity.Warning,
                    "Description is an action summary rather than a trigger specification. Frame when the orchestrator should invoke this skill (e.g. 'Use when...', 'Invoke when...', 'Trigger when...').",
                    id, file, "Start the description with an explicit trigger clause explaining the activation condition.");
            }

            if (FillerRegex().IsMatch(description))
            {
                yield return new Diagnostic("KW-SKILL-LINT-008", Severity.Warning,
                    "Description contains excessive filler phrases or unrouted verbosity. Remove boilerplate like 'This skill is designed to...' or 'allows the user to...' to keep routing metadata concise.",
                    id, file, "State the trigger condition directly without introductory conversational filler.");
            }
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
        if (!DirectiveRegex().IsMatch(skill.InstructionsBody))
            yield return new Diagnostic("KW-SKILL-LINT-004", Severity.Info,
                "Body has no explicit ALWAYS/NEVER/MUST directives. Firm directives help the agent follow the skill consistently.",
                id, file);

        // At least one worked example
        if (!ExampleRegex().IsMatch(skill.InstructionsBody))
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
        IReadOnlyList<Skill> skills = set.Skills;

        // Exact name collisions
        IEnumerable<IGrouping<string, Skill>> byName = skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Frontmatter.Name))
            .GroupBy(s => s.Frontmatter.Name!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (IGrouping<string, Skill> group in byName)
        {
            yield return new Diagnostic("KW-SKILL-LINT-010", Severity.Error,
                $"{group.Count()} skills share the name '{group.Key}'. Names must be unique so the orchestrator can disambiguate.",
                group.Key, string.Join(", ", group.Select(s => s.SkillFilePath)));
        }

        // Description overlap (near-duplicate routing targets)
        List<(Skill Skill, Dictionary<string, double> Vec)> vectors = skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Frontmatter.Description))
            .Select(s => (Skill: s, Vec: TextVectorizer.Vectorize(s.Frontmatter.Description!)))
            .ToList();

        for (int i = 0; i < vectors.Count; i++)
        {
            for (int j = i + 1; j < vectors.Count; j++)
            {
                double sim = TextVectorizer.CosineSimilarity(vectors[i].Vec, vectors[j].Vec);
                if (sim >= OverlapThreshold)
                {
                    string a = vectors[i].Skill.Frontmatter.Name ?? vectors[i].Skill.DirectoryName;
                    string b = vectors[j].Skill.Frontmatter.Name ?? vectors[j].Skill.DirectoryName;
                    yield return new Diagnostic("KW-SKILL-LINT-011", Severity.Warning,
                        $"Descriptions of '{a}' and '{b}' overlap ({sim:P0} lexical similarity). The orchestrator may route ambiguously between them.",
                        $"{a} / {b}", vectors[j].Skill.SkillFilePath,
                        "Sharpen each description's boundary, or merge the skills.");
                }
            }
        }
    }
}
