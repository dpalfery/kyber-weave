namespace KyberWeave.Core.Review;

/// <summary>Parses one council seat's output file into its findings and status.</summary>
public static class SeatOutputParser
{
    /// <summary>Parses the text a seat wrote for one lens.</summary>
    /// <param name="lens">The lens the seat ran, taken from the file name.</param>
    /// <param name="text">The seat's output file contents.</param>
    public static SeatOutput Parse(string lens, string text)
    {
        throw new NotImplementedException();
    }
}

/// <summary>What one council seat produced.</summary>
/// <param name="Lens">The lens the seat ran.</param>
/// <param name="Kind">Whether the seat reported findings, no findings, or was skipped.</param>
/// <param name="Findings">The findings the seat reported, empty when none.</param>
/// <param name="Attestation">The seat's attestation line, when it wrote one.</param>
/// <param name="IndexFallback">Why the seat fell back from the code index, when it did.</param>
/// <param name="Discovery">How the seat found its evidence, when it recorded it.</param>
public sealed record SeatOutput(
    string Lens,
    SeatOutputKind Kind,
    IReadOnlyList<ReviewFinding> Findings,
    string? Attestation,
    string? IndexFallback,
    SeatDiscovery? Discovery);

/// <summary>The outcome a council seat reports.</summary>
public enum SeatOutputKind
{
    /// <summary>The seat reported one or more findings.</summary>
    Findings,

    /// <summary>The seat ran and reported no findings.</summary>
    NoFindings,

    /// <summary>The seat did not run its lens.</summary>
    Skipped
}

/// <summary>How many lookups a seat made through each discovery route.</summary>
/// <param name="CodeGraph">Lookups answered by the code index.</param>
/// <param name="Docs">Lookups answered by the documentation index.</param>
/// <param name="DirectReads">Lookups answered by reading files directly.</param>
public sealed record SeatDiscovery(int CodeGraph, int Docs, int DirectReads);
