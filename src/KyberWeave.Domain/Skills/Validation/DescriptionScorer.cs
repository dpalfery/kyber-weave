using System.Text.RegularExpressions;
using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Validation;

/// <summary>One scored dimension of a description's routing readiness.</summary>
public sealed record ScoreComponent(string Name, int Points, int MaxPoints, string Detail);

/// <summary>The explainable result of scoring a description as routing metadata.</summary>
public sealed record DescriptionScore(int Total, IReadOnlyList<ScoreComponent> Components);

/// <summary>
/// Scores a skill description on how well it functions as ROUTING METADATA — the signal
/// an orchestrator uses to decide when the skill applies. Operationalizes the
/// "write the description like routing metadata" guidance into an explainable rubric.
/// </summary>
/// <remarks>
/// <para>
/// In multi-agent orchestration and LLM tool selection, descriptions serve primarily as
/// <b>activation triggers</b> rather than passive documentation summaries. When an orchestrator
/// evaluates candidate skills against a prompt, it matches user intent against the trigger condition.
/// Descriptions that only summarize task execution actions (e.g., "Generates SQL queries...",
/// "Handles customer data...") describe internal mechanics rather than the trigger criteria,
/// which can lead to semantic collisions and ambiguous routing.
/// </para>
/// <para>
/// The scoring rubric deliberately emphasizes:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Explicit Activation Trigger Phrasing ("Use when...", "Apply when...", "Invoke when..."):</b>
/// Allocates the highest score weight (35 points) to explicit triggering clauses that specify
/// precise caller conditions under which the skill should activate. "Use this to …" names a
/// purpose rather than an activation condition, so it is not scored as a trigger clause.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Negative Boundaries ("Do NOT use for...", "Avoid using for..."):</b>
/// Allocates 20 points for negative scoping boundaries, which prevent false activations and
/// clarify out-of-scope tasks.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Deprecating Action Summaries:</b>
/// Descriptions that lead with functional task verbs ("Generates...", "Handles...") without an
/// explicit trigger clause receive 0 points for the trigger component, reinforcing the
/// trigger-first design requirement.
/// </description>
/// </item>
/// </list>
/// </remarks>
public static partial class DescriptionScorer
{
    [GeneratedRegex(@"\b(use\s+(this\s+)?(skill\s+|agent\s+|tool\s+)?(when|for)|apply\s+when|invoke\s+when|trigger\s+when)\b",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UseWhen();

    [GeneratedRegex(@"\b(do\s+not\s+use|don'?t\s+use|not\s+for|avoid\s+(using\s+)?for|never\s+use|excludes?)\b",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DoNotUse();

    [GeneratedRegex(@"^\s*(helps?\s+with|assists?\s+with|handles?\s+|for\s+all|general\s+)",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex VagueOpening();

    [GeneratedRegex(@"^\s*(use|apply|invoke|trigger|generate|create|validate|triage|classify|route|calculate|compute|look\s*up|retrieve|summari[sz]e|draft|review|extract|analyze|analyse|check|process|handle|resolve|onboard|document|format|convert|parse|fetch|query|escalate|approve|reset|enrich|map)\b",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex StartsWithActionVerb();

    public static DescriptionScore Score(Skill skill)
    {
        string d = (skill.Frontmatter.Description ?? string.Empty).Trim();
        List<ScoreComponent> components = new List<ScoreComponent>();

        if (string.IsNullOrWhiteSpace(d))
        {
            components.Add(new ScoreComponent("Trigger clause", 0, 35, "Empty description — no trigger condition."));
            components.Add(new ScoreComponent("Negative boundary", 0, 20, "Empty description — no negative boundary."));
            components.Add(new ScoreComponent("Specific opening", 0, 15, "Empty description."));
            components.Add(new ScoreComponent("Trigger keywords", 0, 15, "0 distinct content terms."));
            components.Add(new ScoreComponent("Length budget", 0, 15, "Empty description."));
            return new DescriptionScore(0, components);
        }

        // 1. Has an explicit "use when / use for" trigger clause (35)
        components.Add(UseWhen().IsMatch(d)
            ? new ScoreComponent("Trigger clause", 35, 35, "States when to use the skill.")
            : new ScoreComponent("Trigger clause", 0, 35, "No explicit 'Use when…/Use for…' clause — add the triggering condition."));

        // 2. Has an explicit negative boundary "do not use for" (20)
        components.Add(DoNotUse().IsMatch(d)
            ? new ScoreComponent("Negative boundary", 20, 20, "States when NOT to use the skill — prevents over-firing.")
            : new ScoreComponent("Negative boundary", 0, 20, "No 'Do NOT use for…' boundary — the skill may fire on the wrong prompts."));

        // 3. Opens with a trigger / action verb rather than a vague phrase (15)
        if (VagueOpening().IsMatch(d))
            components.Add(new ScoreComponent("Specific opening", 0, 15, "Opens vaguely ('helps with…'). Lead with a concrete action and domain."));
        else if (StartsWithActionVerb().IsMatch(d) || UseWhen().IsMatch(d))
            components.Add(new ScoreComponent("Specific opening", 15, 15, "Opens with a concrete trigger or action verb."));
        else
            components.Add(new ScoreComponent("Specific opening", 7, 15, "Opening is acceptable but could lead with a stronger action verb."));

        // 4. Concrete trigger keywords / specificity via lexical richness (15)
        int distinctTerms = Text.TextVectorizer.Vectorize(d).Count;
        int kwPoints = distinctTerms switch
        {
            >= 8 => 15,
            >= 5 => 10,
            >= 3 => 6,
            >= 1 => 2,
            _ => 0
        };
        components.Add(new ScoreComponent("Trigger keywords", kwPoints, 15,
            $"{distinctTerms} distinct content terms — concrete nouns/keywords help routing."));

        // 5. Length within a routable budget (15): too short = under-specified, too long = noisy
        int lenPoints;
        string lenDetail;
        int len = d.Length;
        if (len < 40) { lenPoints = 5; lenDetail = $"{len} chars — likely under-specified for reliable routing."; }
        else if (len <= 500) { lenPoints = 15; lenDetail = $"{len} chars — within a healthy routing budget."; }
        else if (len <= SpecValidator.DescriptionMaxLength) { lenPoints = 10; lenDetail = $"{len} chars — long; trim to the routing essentials."; }
        else { lenPoints = 0; lenDetail = $"{len} chars — exceeds the spec max of {SpecValidator.DescriptionMaxLength}."; }
        components.Add(new ScoreComponent("Length budget", lenPoints, 15, lenDetail));

        return new DescriptionScore(components.Sum(c => c.Points), components);
    }
}
