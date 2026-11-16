using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Merges the plan's facts into a trigger's fact bag.</summary>
public interface IPlanFacts
{
    /// <summary>Merges the plan facts for <paramref name="classification"/> into <paramref name="facts"/>.</summary>
    ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, string repositoryRoot);
}
