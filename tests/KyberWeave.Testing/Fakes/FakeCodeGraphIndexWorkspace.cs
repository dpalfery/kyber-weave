using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>The index workspace in a unit test: every lock is free and every directory writable, unless a test says otherwise.</summary>
public sealed class FakeCodeGraphIndexWorkspace : ICodeGraphIndexWorkspace
{
    private readonly List<string> _lockPaths = [];

    /// <summary>Gets every lock path asked for, in order.</summary>
    public IReadOnlyList<string> LockPaths => _lockPaths;

    /// <summary>Gets or sets whether a lock is refused, with the reason given by <see cref="LockFailure"/>.</summary>
    public bool RefuseLock { get; set; }

    /// <summary>Gets or sets the reason a refused lock reports.</summary>
    public string LockFailure { get; set; } = "refused for the test";

    /// <summary>Gets or sets whether a directory is writable.</summary>
    public bool Writable { get; set; } = true;

    /// <summary>Gets or sets whether CodeGraph's own lock is held.</summary>
    public bool CodeGraphLockHeld { get; set; }

    /// <inheritdoc />
    public IDisposable? TryAcquireLock(string path, TimeSpan wait, out string? failure)
    {
        _lockPaths.Add(path);
        failure = RefuseLock ? LockFailure : null;
        return RefuseLock ? null : new Handle();
    }

    /// <inheritdoc />
    public bool IsWritable(string directory) => Writable;

    /// <inheritdoc />
    public bool IsCodeGraphLockHeld(string indexDirectory) => CodeGraphLockHeld;

    private sealed class Handle : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
