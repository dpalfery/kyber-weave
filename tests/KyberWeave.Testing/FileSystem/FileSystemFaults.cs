namespace KyberWeave.Tests.FileSystem;

/// <summary>What the fake does when a fault rule matches an operation.</summary>
public enum FileSystemFaultKind
{
    /// <summary>The operation is not performed and throws, leaving the tree as it was.</summary>
    Fail,

    /// <summary>The fake stops at this operation as a killed process would, and keeps its tree for inspection.</summary>
    Kill,
}

/// <summary>One call into the file-system seam, as a fault predicate sees it.</summary>
/// <param name="Name">The seam member's name, such as <c>WriteAllText</c>.</param>
/// <param name="Path">The path the member was given, when it has one.</param>
public readonly record struct FileSystemOperation(string Name, string? Path);

/// <summary>Raised by a fake that was killed at an operation. Recovery is simulated with <see cref="InMemoryFileSystem.ClearFaults"/>.</summary>
public sealed class InMemoryFileSystemKilledException : Exception
{
    /// <summary>Creates the exception.</summary>
    public InMemoryFileSystemKilledException()
        : base("The in-memory file system was killed at this operation.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public InMemoryFileSystemKilledException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public InMemoryFileSystemKilledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A rule that fails or kills the fake at one operation, by ordinal or by predicate.</summary>
/// <remarks>
/// The ordinal is the one-based count of seam calls the fake has seen. The count includes reads as
/// well as writes, so a test that injects at "the third write" either passes a predicate over
/// <see cref="FileSystemOperation.Name"/> or counts every call it makes.
/// </remarks>
public sealed class FileSystemFault
{
    private FileSystemFault(FileSystemFaultKind kind, int? operationNumber, Func<FileSystemOperation, bool>? predicate, Exception? failure)
    {
        Kind = kind;
        OperationNumber = operationNumber;
        Predicate = predicate;
        Failure = failure;
    }

    /// <summary>Gets what happens when the rule matches.</summary>
    public FileSystemFaultKind Kind { get; }

    /// <summary>Gets the one-based operation number that triggers the rule, or null for a predicate rule.</summary>
    public int? OperationNumber { get; }

    /// <summary>Gets the predicate that triggers the rule, or null for an ordinal rule.</summary>
    public Func<FileSystemOperation, bool>? Predicate { get; }

    /// <summary>Gets the exception a <see cref="FileSystemFaultKind.Fail"/> rule throws.</summary>
    public Exception? Failure { get; }

    /// <summary>Fails at one operation.</summary>
    /// <param name="operationNumber">The one-based operation number.</param>
    /// <param name="failure">The exception to throw; an <see cref="IOException"/> when null.</param>
    /// <returns>The rule.</returns>
    public static FileSystemFault FailAt(int operationNumber, Exception? failure = null) =>
        new(FileSystemFaultKind.Fail, Positive(operationNumber), null, failure);

    /// <summary>Kills the fake at one operation.</summary>
    /// <param name="operationNumber">The one-based operation number.</param>
    /// <returns>The rule.</returns>
    public static FileSystemFault KillAt(int operationNumber) =>
        new(FileSystemFaultKind.Kill, Positive(operationNumber), null, null);

    /// <summary>Fails every operation the predicate matches.</summary>
    /// <param name="predicate">Chooses the operations to fail.</param>
    /// <param name="failure">The exception to throw; an <see cref="IOException"/> when null.</param>
    /// <returns>The rule.</returns>
    public static FileSystemFault FailWhen(Func<FileSystemOperation, bool> predicate, Exception? failure = null) =>
        new(FileSystemFaultKind.Fail, null, predicate ?? throw new ArgumentNullException(nameof(predicate)), failure);

    /// <summary>Kills the fake at the first operation the predicate matches.</summary>
    /// <param name="predicate">Chooses the operation to kill at.</param>
    /// <returns>The rule.</returns>
    public static FileSystemFault KillWhen(Func<FileSystemOperation, bool> predicate) =>
        new(FileSystemFaultKind.Kill, null, predicate ?? throw new ArgumentNullException(nameof(predicate)), null);

    internal bool Matches(int operationNumber, FileSystemOperation operation) =>
        OperationNumber is { } number ? number == operationNumber : Predicate is not null && Predicate(operation);

    private static int Positive(int operationNumber) =>
        operationNumber > 0
            ? operationNumber
            : throw new ArgumentOutOfRangeException(nameof(operationNumber), operationNumber, "Operation numbers start at 1.");
}
