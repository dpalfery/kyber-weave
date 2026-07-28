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
        var promptVec = TextVectorizer.Vectorize(prompt);
        var matrix = agentSet.GetRoleHarnessMatrix();

        var ranked = matrix.Select(entry =>
            {
                var role = entry.Key;
                var sampleAgent = entry.Value.Values.FirstOrDefault();
                var routingText = sampleAgent != null
                    ? $"{role} {sampleAgent.Description}"
                    : role;

                var score = TextVectorizer.CosineSimilarity(promptVec, TextVectorizer.Vectorize(routingText));
                return new RoutingCandidate(role, score);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.SkillName, StringComparer.Ordinal)
            .ToList();

        var top = ranked.FirstOrDefault();
        var fired = top is not null && top.Score >= fireThreshold;
        return new AgentRoutingResult(fired ? top!.SkillName : null, fired, ranked);
    }
}
