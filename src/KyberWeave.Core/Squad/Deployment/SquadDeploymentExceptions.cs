using JetBrains.Annotations;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Raised when a deployment would overwrite a path outside its receipt authority.</summary>
public sealed class SquadDeploymentConflictException : InvalidOperationException
{
    [UsedImplicitly]
    public SquadDeploymentConflictException()
    {
    }

    public SquadDeploymentConflictException(string message)
        : base(message)
    {
    }

    public SquadDeploymentConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a deployment path escapes its declared target root.</summary>
public sealed class SquadPathContainmentException : InvalidOperationException
{
    [UsedImplicitly]
    public SquadPathContainmentException()
    {
    }

    public SquadPathContainmentException(string message)
        : base(message)
    {
    }

    [UsedImplicitly]
    public SquadPathContainmentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
