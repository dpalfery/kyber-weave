namespace KyberDash.Data.Collector.Sources;

/// <summary>
/// Pure mapping contract that translates a loaded <see cref="NativeSourceUnit"/> into a normalized
/// <see cref="CollectedBatch"/> without touching the filesystem or ambient clock.
/// </summary>
public interface IHarnessMapper
{
    /// <summary>Canonical harness identifier emitted by this mapper.</summary>
    string HarnessId { get; }

    /// <summary>Maps <paramref name="unit"/> into sessions, records, and problems stamped at <paramref name="importedAtUtc"/>.</summary>
    /// <param name="unit">Native source unit produced by an <see cref="IHarnessSourceReader"/>.</param>
    /// <param name="importedAtUtc">Caller-supplied UTC import timestamp.</param>
    /// <returns>Static collector output batch for <paramref name="unit"/>.</returns>
    CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc);
}
