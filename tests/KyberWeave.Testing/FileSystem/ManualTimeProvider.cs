namespace KyberWeave.Tests.FileSystem;

/// <summary>A clock a test sets and advances by hand, so a fake's timestamps are exact.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    /// <summary>Creates the clock at a fixed instant.</summary>
    /// <param name="now">The instant the clock reports.</param>
    public ManualTimeProvider(DateTimeOffset now) => Now = now;

    /// <summary>Gets the instant the clock reports.</summary>
    public DateTimeOffset Now { get; private set; }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="delta">How far to move it.</param>
    public void Advance(TimeSpan delta) => Now += delta;
}
