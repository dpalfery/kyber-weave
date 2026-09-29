using JetBrains.Annotations;

namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>Raised when a status-line deployment would touch a path outside its receipt authority.</summary>
/// <remarks>
/// One type covers every refusal this namespace makes about a location — an unmanaged occupant, a
/// path inside an auto-load directory, a path that is a harness settings file, a receipt that
/// records a path outside the staging root, and a duplicate planned path — because they share one
/// remedy from the operator's side: move the conflicting file aside, or delete the stale receipt,
/// and deploy again. Mirrors <c>SquadDeploymentConflictException</c>.
/// </remarks>
public sealed class StatusLineDeploymentConflictException : InvalidOperationException
{
    [UsedImplicitly]
    public StatusLineDeploymentConflictException()
    {
    }

    public StatusLineDeploymentConflictException(string message)
        : base(message)
    {
    }

    public StatusLineDeploymentConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
