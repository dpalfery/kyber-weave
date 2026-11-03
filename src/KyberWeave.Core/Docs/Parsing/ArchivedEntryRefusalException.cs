using JetBrains.Annotations;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>Why <see cref="DocumentLoader.LoadArchivedDocument"/> refused an archived entry.</summary>
public enum ArchivedEntryRefusalReason
{
    /// <summary>
    /// Conventional exception constructors do not record a reason. The loader does not throw those.
    /// </summary>
    Unspecified = 0,

    /// <summary>The path leaves the repository root.</summary>
    EscapesRepositoryRoot = 1,

    /// <summary>Reaching the file would follow a symbolic link.</summary>
    CrossesSymbolicLink = 2,
}

/// <summary>
/// Raised when an archived entry cannot be read because its path is outside the
/// repository or crosses a symbolic link.
/// </summary>
/// <remarks>
/// The closeout gate classifies this by <see cref="Reason"/>. The message is for the
/// reader; a reword must not decide whether the refusal becomes a finding or an
/// unhandled error.
/// </remarks>
public sealed class ArchivedEntryRefusalException : InvalidOperationException
{
    /// <summary>
    /// Required by <see cref="Exception"/> serialization and by CA1032. ReSharper reports
    /// it unused because nothing in this assembly constructs it by name; [UsedImplicitly]
    /// is how those two analyzers are told they are both right.
    /// </summary>
    [UsedImplicitly]
    public ArchivedEntryRefusalException()
    {
    }

    [UsedImplicitly]
    public ArchivedEntryRefusalException(string message)
        : base(message)
    {
    }

    [UsedImplicitly]
    public ArchivedEntryRefusalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>A refusal the loader raises, carrying the reason the gate classifies on.</summary>
    public ArchivedEntryRefusalException(ArchivedEntryRefusalReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Why the entry was refused. Unset on the conventional constructors.</summary>
    public ArchivedEntryRefusalReason Reason { get; }
}
