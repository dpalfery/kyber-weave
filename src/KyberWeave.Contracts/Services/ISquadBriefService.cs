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

    /// <summary>Finds a reference file under the name the map gives it, or under its installed <c>.markdown</c> name.</summary>
    /// <param name="referencesDirectory">The directory holding the installed reference files.</param>
    /// <param name="file">The file name the map gives.</param>
    /// <returns>The path of the file that exists.</returns>
    /// <exception cref="BriefMapException">Neither name exists; the message names both paths.</exception>
    string ResolveFile(string referencesDirectory, string file);
}
