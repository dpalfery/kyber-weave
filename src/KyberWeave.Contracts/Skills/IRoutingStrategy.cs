using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Routing;

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
