namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The records of an append-only Arbiter log with what the read could not show: lines that were not a record, a window
/// that cut older records off, and a log that exists but could not be read.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <param name="Records">The records in the window, in append order.</param>
/// <param name="MalformedLines">How many whole lines were not a record.</param>
/// <param name="Truncated">Whether the log was longer than the read window, so older records were not read.</param>
/// <param name="Unreadable">Why a log that exists could not be read; null otherwise.</param>
public sealed record ArbiterLogRead<T>(
    IReadOnlyList<T> Records,
    int MalformedLines,
    bool Truncated,
    string? Unreadable);
