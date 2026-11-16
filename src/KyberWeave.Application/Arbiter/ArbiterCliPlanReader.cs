using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// The plan facts for a dry run, read from the plan file the dispatch headers name. Plan and task identity come from
/// <c>PLAN_FILE</c> and <c>TASK</c> only; nothing is inferred from the plan index. The header is caller-controlled, so
/// a path that does not resolve inside the repository reads as <c>plan.exists: false</c>.
/// </summary>
/// <remarks>
/// Internal rather than private so the tests reach this reader itself. The hook host's equivalent is internal for the
/// same reason, and a containment rule proved against a stand-in reader is not a proof about the reader that ships.
/// </remarks>
/// <param name="planFacts">Reads the plan file's facts.</param>
/// <param name="repositoryRoot">The repository the plan path must resolve inside.</param>
internal sealed class ArbiterCliPlanReader(IPlanFacts planFacts, string repositoryRoot) : IArbiterPlanReader
{
    /// <inheritdoc />
    public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
        planFacts.Enrich(facts, classification, repositoryRoot);
}

/// <summary>
/// The git facts for an offline dry run: the base bag already carries every fact the trigger supplies (absent as null),
/// and with no live repository state there is nothing truthful to add, so the bag passes through unchanged and absent
/// facts keep satisfying <c>exists: false</c>.
/// </summary>
internal sealed class ArbiterCliGitFacts : IArbiterGitFacts
{
    /// <inheritdoc />
    public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
        facts;
}
