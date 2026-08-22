using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Routing;

/// <summary>A single skill ranked for a prompt.</summary>
public sealed record RoutingCandidate(string SkillName, double Score);

/// <summary>
/// The predicted routing outcome for a prompt: the selected skill (or none), the full
/// ranked list, and whether any skill cleared the firing threshold.
/// </summary>
public sealed record RoutingResult(string? SelectedSkill, bool Fired, IReadOnlyList<RoutingCandidate> Ranked)
{
    public double Margin => Ranked.Count >= 2 ? Ranked[0].Score - Ranked[1].Score : Ranked.Count == 1 ? Ranked[0].Score : 0;
}

/// <summary>
/// Predicts which skill an orchestrator would load for a prompt. Implementations may be
/// offline/lexical (default, CI-safe) or backed by embeddings / an LLM judge. This is a
/// pre-deployment signal and regression test — it approximates, not replicates, the live
/// orchestrator, so still confirm against the agent's reasoning view.
/// </summary>
public interface IRoutingStrategy
{
    string Name { get; }
    RoutingResult Route(string prompt, SkillSet skills);
}
