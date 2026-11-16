using KyberWeave.Contracts.Services;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Builds briefs by reading the installed reference files.</summary>
public sealed class SquadBriefService : ISquadBriefService
{
    /// <inheritdoc />
    public BriefResult Assemble(BriefMap map, string agent, BriefFacts facts, string referencesDirectory) =>
        BriefAssembler.Assemble(map, agent, facts, referencesDirectory);
}
