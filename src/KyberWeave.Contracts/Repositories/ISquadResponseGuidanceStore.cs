using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads and deploys the response guidance Squad keeps in the user's home.</summary>
public interface ISquadResponseGuidanceStore
{
    /// <summary>Reads the guidance in effect: the user's own copy, or the shipped default.</summary>
    string ReadEffective(string homeDirectory, ICollection<string> warnings);

    /// <summary>Creates a deployer that writes the guidance under a home-owned root.</summary>
    ISquadResponseGuidanceDeployer CreateDeployer(string homeStateRoot);

    /// <summary>Tells whether a failure should degrade to a warning rather than fail the run.</summary>
    bool IsDegradableFailure(Exception exception);
}

/// <summary>Writes the response guidance file under the user's home.</summary>
public interface ISquadResponseGuidanceDeployer
{
    /// <summary>Writes the guidance, returning the record of the file or null when none applies.</summary>
    SquadHomeFile? Deploy(IReadOnlyList<SquadHomeFile>? previous, ICollection<string> warnings);
}
