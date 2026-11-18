using System.Reflection;
using System.Runtime.ExceptionServices;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Testing.Update;

/// <summary>The exception a <see cref="LatchingFaultFileSystem"/> raises once its process has been killed.</summary>
public sealed class SelfUpdateCrashInjectedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SelfUpdateCrashInjectedException()
        : base("The process was killed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was killed, and where.</param>
    public SelfUpdateCrashInjectedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was killed, and where.</param>
    /// <param name="innerException">The cause.</param>
    public SelfUpdateCrashInjectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A real file system that dies at one chosen operation and stays dead: the chosen call and every later call throw
/// without touching the disk, as a killed process performs nothing more, until <see cref="Restart"/>.
/// </summary>
/// <remarks>
/// Latching is the point. The update's rollback and cleanup blocks run when something throws, so a wrapper that fails
/// once lets them run for real and the test proves a handled error instead of a kill. Every seam member goes through
/// one dispatch point, so the wrapper needs no member list of its own. It counts the same calls as
/// <c>InMemoryFileSystem</c>, so one sweep covers both.
/// </remarks>
public class LatchingFaultFileSystem : DispatchProxy
{
    private IWritableFileSystem _inner = null!;
    private int _killAt;
    private Func<string, string?, bool>? _failWhen;
    private readonly List<string> _temporaryDirectories = [];

    /// <summary>Gets how many seam calls were made, counting the killing one.</summary>
    public int OperationCount { get; private set; }

    /// <summary>Gets whether the process was killed and has not been restarted.</summary>
    public bool IsKilled { get; private set; }

    /// <summary>Gets the temporary directories the wrapped file system created, so a test can remove them.</summary>
    public IReadOnlyList<string> TemporaryDirectories => _temporaryDirectories;

    /// <summary>Wraps a file system.</summary>
    /// <param name="inner">The real file system.</param>
    /// <returns>The file system the code under test uses.</returns>
    public static IWritableFileSystem Wrap(IWritableFileSystem inner)
    {
        IWritableFileSystem proxy = Create<IWritableFileSystem, LatchingFaultFileSystem>();
        ((LatchingFaultFileSystem)(object)proxy)._inner = inner;
        return proxy;
    }

    /// <summary>Gets the control surface of a wrapped file system.</summary>
    /// <param name="wrapped">What <see cref="Wrap"/> returned.</param>
    /// <returns>The wrapper.</returns>
    public static LatchingFaultFileSystem Control(IWritableFileSystem wrapped) => (LatchingFaultFileSystem)(object)wrapped;

    /// <summary>Kills the process at one operation.</summary>
    /// <param name="ordinal">The one-based operation number, counted from the first call ever made.</param>
    public void KillAt(int ordinal) => _killAt = ordinal;

    /// <summary>Fails every operation the predicate matches, once per call, without killing.</summary>
    /// <param name="predicate">Gets the member name and its first path.</param>
    public void FailWhen(Func<string, string?, bool> predicate) => _failWhen = predicate;

    /// <summary>Restarts the process: the plan and the latch go, the disk stays.</summary>
    public void Restart()
    {
        _killAt = 0;
        _failWhen = null;
        IsKilled = false;
    }

    /// <summary>Counts one operation, and dies or fails when the plan says so.</summary>
    /// <param name="name">The operation's name.</param>
    /// <param name="path">The path it works on.</param>
    public void Enter(string name, string? path)
    {
        if (IsKilled)
        {
            throw new SelfUpdateCrashInjectedException($"The process was already killed ({name} '{path}').");
        }

        int number = ++OperationCount;
        if (number == _killAt)
        {
            IsKilled = true;
            throw new SelfUpdateCrashInjectedException($"The process was killed at operation {number} ({name} '{path}').");
        }

        if (_failWhen?.Invoke(name, path) == true)
        {
            throw new IOException($"Operation {number} failed ({name} '{path}').");
        }
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        string? path = args?.OfType<string>().FirstOrDefault();
        Enter(targetMethod.Name, path);

        try
        {
            object? result = targetMethod.Invoke(_inner, args);
            if (targetMethod.Name == nameof(IWritableFileSystem.CreateTemporaryDirectory) && result is string created)
            {
                _temporaryDirectories.Add(created);
            }

            if (result is Stream raw && targetMethod.Name is nameof(IWritableFileSystem.OpenWriteNew)
                or nameof(IWritableFileSystem.OpenExclusive) or nameof(IWritableFileSystem.OpenAppendShared))
            {
                return new KillableWriteStream(raw, this, path);
            }

            return result;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
