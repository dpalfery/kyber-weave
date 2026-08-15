using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Skills.Routing;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Agents.Routing;

public sealed record AgentRoutingResult(string? SelectedRole, bool Fired, IReadOnlyList<RoutingCandidate> Ranked);

/// <summary>
/// Predicts which agent role an orchestrator will select for a given user prompt.
/// </summary>
public static class AgentRoutingEvaluator
{
    public static AgentRoutingResult Route(string prompt, AgentSet agentSet, double fireThreshold = 0.08)
    {
        Dictionary<string, double> promptVec = TextVectorizer.Vectorize(prompt);
        IReadOnlyDictionary<string, Dictionary<HarnessKind, AgentModel>> matrix = agentSet.GetRoleHarnessMatrix();

        List<RoutingCandidate> ranked = matrix.Select(entry =>
            {
                string role = entry.Key;
                AgentModel? sampleAgent = entry.Value.Values.FirstOrDefault();
                string routingText = sampleAgent != null
                    ? $"{role} {sampleAgent.Description}"
                    : role;

                double score = TextVectorizer.CosineSimilarity(promptVec, TextVectorizer.Vectorize(routingText));
                return new RoutingCandidate(role, score);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.SkillName, StringComparer.Ordinal)
            .ToList();

        RoutingCandidate? top = ranked.FirstOrDefault();
        bool fired = top is not null && top.Score >= fireThreshold;
        return new AgentRoutingResult(fired ? top!.SkillName : null, fired, ranked);
    }
}
