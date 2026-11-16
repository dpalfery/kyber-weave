using KyberWeave.Core.Squad.Briefs;

namespace KyberWeave.Contracts.Services;

/// <summary>Builds the brief of an agent's reference files that applies to a run.</summary>
public interface ISquadBriefService
{
    /// <summary>Builds the brief for an agent.</summary>
    /// <param name="map">The map of files and headings to facts.</param>
    /// <param name="agent">The agent whose reference files are read.</param>
    /// <param name="facts">The run's facts; unknown ones keep their sections.</param>
    /// <param name="referencesDirectory">The directory holding that agent's installed reference files.</param>
    /// <returns>The brief, with what was left out.</returns>
    BriefResult Assemble(BriefMap map, string agent, BriefFacts facts, string referencesDirectory);
}
