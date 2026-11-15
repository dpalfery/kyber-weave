namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Raised when this assembly carries no readable default response guidance, so a global update has
///     nothing to deploy.
/// </summary>
/// <remarks>
///     An <see cref="IOException" /> subclass on purpose: an embedded stream that cannot be opened is
///     the same class of failure as a file that cannot be read, and it belongs on the degradable side of
///     <c>SquadResponseGuidanceDeployer.IsDegradableFailure</c> so a broken build costs a warning
///     rather than the whole deployment. The type is distinct so the warning can name a defect a rerun
///     cannot fix, which a plain <see cref="IOException" /> from the filesystem must not claim.
/// </remarks>
public sealed class SquadShippedGuidanceUnavailableException : IOException
{
    public SquadShippedGuidanceUnavailableException()
    {
    }

    public SquadShippedGuidanceUnavailableException(string message)
        : base(message)
    {
    }

    public SquadShippedGuidanceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
