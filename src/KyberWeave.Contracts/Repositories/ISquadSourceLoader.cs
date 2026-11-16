using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Loads and validates canonical Squad source from a directory.</summary>
public interface ISquadSourceLoader
{
    /// <summary>Loads the source under a root.</summary>
    SquadSource Load(string root);
}
