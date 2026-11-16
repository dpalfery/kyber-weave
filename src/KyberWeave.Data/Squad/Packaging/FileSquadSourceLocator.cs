using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>Finds the canonical Squad source on disk through <see cref="SquadPackSourceLocator"/>.</summary>
public sealed class FileSquadSourceLocator : ISquadSourceLocator
{
    /// <inheritdoc />
    public string? Resolve(string workingDirectory) => SquadPackSourceLocator.Resolve(workingDirectory);
}
