using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Supplies the per-user locations used by Squad state.</summary>
public interface ISquadUserPaths
{
    /// <summary>The per-user application data directory that holds legacy global Squad state.</summary>
    string ApplicationDataDirectory { get; }

    /// <summary>
    ///     The user's home directory. Home-level Squad state lives beneath it at
    ///     <c>.kyber-weave/squad</c>, so it does not depend on the platform application data folder.
    /// </summary>
    string HomeDirectory { get; }
}
