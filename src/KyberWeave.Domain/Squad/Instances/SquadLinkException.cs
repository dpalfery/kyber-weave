namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Exception thrown when a Squad file link or folder link operation fails.
/// </summary>
public sealed class SquadLinkException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SquadLinkException"/> class.
    /// </summary>
    public SquadLinkException()
        : base()
    {
        LinkPath = string.Empty;
        TargetPath = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SquadLinkException"/> class with a message.
    /// </summary>
    /// <param name="message">The description of the failure.</param>
    public SquadLinkException(string message)
        : base(message)
    {
        LinkPath = string.Empty;
        TargetPath = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SquadLinkException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The description of the failure.</param>
    /// <param name="innerException">The underlying exception that caused the failure.</param>
    public SquadLinkException(string message, Exception innerException)
        : base(message, innerException)
    {
        LinkPath = string.Empty;
        TargetPath = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SquadLinkException"/> class with link and target paths.
    /// </summary>
    /// <param name="message">The human-readable description of the failure.</param>
    /// <param name="linkPath">The path of the link being created or evaluated.</param>
    /// <param name="targetPath">The expected or actual target path of the link.</param>
    /// <param name="innerException">The underlying exception that caused the failure, if any.</param>
    public SquadLinkException(string message, string linkPath, string targetPath, Exception? innerException = null)
        : base(message, innerException)
    {
        LinkPath = linkPath;
        TargetPath = targetPath;
    }

    /// <summary>
    /// Gets the path of the link entry that triggered the exception.
    /// </summary>
    public string LinkPath { get; }

    /// <summary>
    /// Gets the target path associated with the link operation.
    /// </summary>
    public string TargetPath { get; }
}
