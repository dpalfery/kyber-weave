using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The git-derived facts the evaluator merges into each trigger's fact bag.</summary>
/// <remarks>
/// A port the host implements; the evaluator takes it as a constructor argument and
/// constructs nothing itself. Tests inject a pass-through stub.
/// </remarks>
public interface IArbiterGitFacts
{
    /// <summary>Merges git-derived facts into <paramref name="facts"/>.</summary>
    ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev);
}

/// <summary>Names the run a dispatch belongs to, for the git facts that can read the run records.</summary>
public interface IArbiterRunScopeSource
{
    /// <summary>The run that works in <paramref name="directory"/>, or null when no recorded run does.</summary>
    /// <param name="directory">The directory the dispatch is evaluated in.</param>
    ArbiterRunScope? RunScopeOf(string? directory);
}

/// <summary>The plan facts the evaluator merges into each trigger's fact bag.</summary>
/// <remarks>
/// A port the host implements; the evaluator takes it as a constructor argument and
/// constructs nothing itself. Tests inject a pass-through stub.
/// </remarks>
public interface IArbiterPlanReader
{
    /// <summary>Merges plan-derived facts into <paramref name="facts"/>.</summary>
    ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification);
}
