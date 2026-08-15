using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>Production implementation of user paths for global Squad state.</summary>
public sealed class SquadUserPaths : ISquadUserPaths
{
    /// <summary>The stateless shared instance.</summary>
    public static SquadUserPaths Instance { get; } = new();

    private SquadUserPaths()
    {
    }

    /// <inheritdoc />
    public string ApplicationDataDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
}
