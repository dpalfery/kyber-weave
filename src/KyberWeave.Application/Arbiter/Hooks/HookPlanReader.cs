using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The hook's plan facts, read from the plan file the dispatch headers name through
/// <see cref="IPlanFacts"/>, the reader every host shares.
/// </summary>
internal sealed class HookPlanReader(IPlanFacts planFacts, string repositoryRoot) : IArbiterPlanReader
{
    /// <inheritdoc/>
    public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
        planFacts.Enrich(facts, classification, repositoryRoot);
}
