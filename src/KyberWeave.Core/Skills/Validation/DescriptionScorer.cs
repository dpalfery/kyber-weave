using System.Text.RegularExpressions;
using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Validation;

/// <summary>One scored dimension of a description's routing readiness.</summary>
public sealed record ScoreComponent(string Name, int Points, int MaxPoints, string Detail);

/// <summary>The explainable result of scoring a description as routing metadata.</summary>
public sealed record DescriptionScore(int Total, IReadOnlyList<ScoreComponent> Components)
{
    public int Max => Components.Sum(c => c.MaxPoints);
}

/// <summary>
/// Scores a skill description on how well it functions as ROUTING METADATA — the signal
/// an orchestrator uses to decide when the skill applies. Operationalizes the
/// "write the description like routing metadata" guidance into an explainable rubric.
/// </summary>
public static class DescriptionScorer
{
    private static readonly Regex UseWhen =
        new(@"\b(use\s+(this\s+)?(skill\s+)?(when|for|to)|apply\s+when|invoke\s+when|trigger\s+when)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DoNotUse =
        new(@"\b(do\s+not\s+use|don'?t\s+use|not\s+for|avoid\s+(using\s+)?for|never\s+use|excludes?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VagueOpening =
        new(@"^\s*(helps?\s+with|assists?\s+with|handles?\s+|for\s+all|general\s+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StartsWithActionVerb =
        new(@"^\s*(use|generate|create|validate|triage|classify|route|calculate|compute|look\s*up|retrieve|summari[sz]e|draft|review|extract|analyze|analyse|check|process|handle|resolve|onboard|document|format|convert|parse|fetch|query|escalate|approve|reset|enrich|map)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DescriptionScore Score(Skill skill)
    {
        var d = (skill.Frontmatter.Description ?? string.Empty).Trim();
        var components = new List<ScoreComponent>();

        // 1. Has an explicit "use when / use for" trigger clause (25)
        components.Add(UseWhen.IsMatch(d)
            ? new ScoreComponent("Trigger clause", 25, 25, "States when to use the skill.")
            : new ScoreComponent("Trigger clause", 0, 25, "No explicit 'Use when…/Use for…' clause — add the triggering condition."));

        // 2. Has an explicit negative boundary "do not use for" (20)
        components.Add(DoNotUse.IsMatch(d)
            ? new ScoreComponent("Negative boundary", 20, 20, "States when NOT to use the skill — prevents over-firing.")
            : new ScoreComponent("Negative boundary", 0, 20, "No 'Do NOT use for…' boundary — the skill may fire on the wrong prompts."));

        // 3. Opens with an action verb rather than a vague phrase (15)
        if (VagueOpening.IsMatch(d))
            components.Add(new ScoreComponent("Specific opening", 0, 15, "Opens vaguely ('helps with…'). Lead with a concrete action and domain."));
        else if (StartsWithActionVerb.IsMatch(d))
            components.Add(new ScoreComponent("Specific opening", 15, 15, "Opens with a concrete action verb."));
        else
            components.Add(new ScoreComponent("Specific opening", 7, 15, "Opening is acceptable but could lead with a stronger action verb."));

        // 4. Concrete trigger keywords / specificity via lexical richness (20)
        var distinctTerms = Text.TextVectorizer.Vectorize(d).Count;
        var kwPoints = distinctTerms switch
        {
            >= 8 => 20,
            >= 5 => 14,
            >= 3 => 8,
            _ => 2
        };
        components.Add(new ScoreComponent("Trigger keywords", kwPoints, 20,
            $"{distinctTerms} distinct content terms — concrete nouns/keywords help routing."));

        // 5. Length within a routable budget (20): too short = under-specified, too long = noisy
        int lenPoints;
        string lenDetail;
        var len = d.Length;
        if (len == 0) { lenPoints = 0; lenDetail = "Empty description."; }
        else if (len < 40) { lenPoints = 6; lenDetail = $"{len} chars — likely under-specified for reliable routing."; }
        else if (len <= 500) { lenPoints = 20; lenDetail = $"{len} chars — within a healthy routing budget."; }
        else if (len <= SpecValidator.DescriptionMaxLength) { lenPoints = 12; lenDetail = $"{len} chars — long; trim to the routing essentials."; }
        else { lenPoints = 0; lenDetail = $"{len} chars — exceeds the spec max of {SpecValidator.DescriptionMaxLength}."; }
        components.Add(new ScoreComponent("Length budget", lenPoints, 20, lenDetail));

        return new DescriptionScore(components.Sum(c => c.Points), components);
    }
}
