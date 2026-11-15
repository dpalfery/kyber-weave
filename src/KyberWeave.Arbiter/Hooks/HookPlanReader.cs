using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The hook's plan facts, read from the plan file the dispatch headers name through
/// <see cref="PlanFacts"/>, the reader every host shares.
/// </summary>
internal sealed class HookPlanReader(string repositoryRoot) : IArbiterPlanReader
{
    /// <inheritdoc/>
    public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
        PlanFacts.Enrich(facts, classification, repositoryRoot);
}
