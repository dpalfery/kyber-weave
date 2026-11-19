using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Briefs;

namespace KyberWeave.Contracts.Services;

/// <summary>Resolves a brief's facts and reference directory, then assembles it.</summary>
public interface ISquadBriefBuilder
{
    /// <summary>
    /// Builds the brief. A failure is a message, not an exception: a refused fact, an unknown agent, a plan
    /// that cannot be read, or reference files that cannot be found each say what to change.
    /// </summary>
    /// <param name="request">What the brief is built from.</param>
    /// <param name="result">The brief when it was built.</param>
    /// <param name="refusal">What to change when it was not.</param>
    /// <returns>True when the brief was built.</returns>
    bool TryBuild(SquadBriefRequest request, out BriefResult? result, out string refusal);

    /// <summary>
    /// Resolves the autonomy a run is on the way a brief does: the value given, then the repository's root
    /// <c>AGENTS.md</c> Config Reg, then the configured setting. No default is applied.
    /// </summary>
    /// <param name="repositoryRoot">The repository whose <c>AGENTS.md</c> is read.</param>
    /// <param name="given">The value given for this run, or null.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <returns><c>supervised</c> or <c>autonomous</c>, or null when none of the three sets one.</returns>
    /// <exception cref="ArgumentException"><paramref name="given"/> is set and is not a value of autonomy.</exception>
    string? ResolveAutonomy(string repositoryRoot, string? given, KyberWeaveConfig config);

    /// <summary>Checks the facts a caller may give on the command line against the vocabulary.</summary>
    /// <param name="path">The route, or null.</param>
    /// <param name="autonomy">The autonomy, or null.</param>
    /// <param name="refusal">Why a fact was refused.</param>
    /// <returns>True when both facts are acceptable.</returns>
    bool AcceptsFacts(string? path, string? autonomy, out string refusal);
}
