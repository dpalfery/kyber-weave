using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Routing;

/// <summary>Runs a routing eval set against a skill set and reports accuracy.</summary>
public sealed class RoutingEvaluator(IRoutingStrategy strategy)
{
    public RoutingEvalSummary Evaluate(RoutingEvalFile evalFile, SkillSet skills)
    {
        List<RoutingCaseResult> results = new List<RoutingCaseResult>();
        foreach (RoutingEvalCase c in evalFile.Cases)
        {
            RoutingResult result = strategy.Route(c.Prompt, skills);
            bool passed = c.ExpectsNoFire
                ? !result.Fired
                : result.Fired && string.Equals(result.SelectedSkill, c.Expected, StringComparison.OrdinalIgnoreCase);
            results.Add(new RoutingCaseResult(c, result, passed));
        }

        return new RoutingEvalSummary(results);
    }
}
