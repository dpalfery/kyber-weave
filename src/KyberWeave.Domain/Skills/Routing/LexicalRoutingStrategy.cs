using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Skills.Routing;

/// <summary>
/// Default, dependency-free routing strategy. Scores each skill by lexical cosine
/// similarity between the prompt and the skill's name + description (the routing metadata
/// the orchestrator actually sees). Deterministic and offline so it runs in CI with no
/// API key. Swap in an embedding or LLM-judge strategy for higher fidelity.
/// </summary>
public sealed class LexicalRoutingStrategy : IRoutingStrategy
{
    public string Name => "lexical";

    /// <summary>Minimum similarity for a skill to be considered "fired".</summary>
    public double FireThreshold { get; init; } = 0.08;

    public RoutingResult Route(string prompt, SkillSet skills)
    {
        Dictionary<string, double> promptVec = TextVectorizer.Vectorize(prompt);

        List<RoutingCandidate> ranked = skills.Skills
            .Select(s =>
            {
                string routingText = $"{s.Frontmatter.Name} {s.Frontmatter.Description}";
                double score = TextVectorizer.CosineSimilarity(promptVec, TextVectorizer.Vectorize(routingText));
                return new RoutingCandidate(s.Frontmatter.Name ?? s.DirectoryName, score);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.SkillName, StringComparer.Ordinal)
            .ToList();

        RoutingCandidate? top = ranked.FirstOrDefault();
        bool fired = top is not null && top.Score >= FireThreshold;
        return new RoutingResult(fired ? top!.SkillName : null, fired, ranked);
    }
}
