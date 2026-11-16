namespace KyberWeave.Contracts.Repositories;

/// <summary>Keeps the lenses a review cycle decided not to seat, with each reason, in the cycle folder.</summary>
public interface INotSeatedStore
{
    /// <summary>Adds decisions to the cycle's record, keeping the decisions already in it.</summary>
    /// <param name="cycleFolder">The cycle folder.</param>
    /// <param name="decisions">Lens name to reason.</param>
    /// <exception cref="IOException">The record could not be written.</exception>
    void Add(string cycleFolder, IReadOnlyDictionary<string, string> decisions);

    /// <summary>Records decisions as the current answer for the owned lenses, removing an earlier entry that no longer holds.</summary>
    /// <param name="cycleFolder">The cycle folder.</param>
    /// <param name="owned">The lenses the caller decides.</param>
    /// <param name="decisions">Lens name to reason.</param>
    /// <exception cref="IOException">The record could not be written.</exception>
    void Replace(string cycleFolder, IEnumerable<string> owned, IReadOnlyDictionary<string, string> decisions);

    /// <summary>The cycle's recorded decisions; empty when there is no record or it cannot be read.</summary>
    /// <param name="cycleFolder">The cycle folder.</param>
    IReadOnlyDictionary<string, string> Read(string cycleFolder);

    /// <summary>The newest <c>cycle-&lt;n&gt;</c> folder under a review folder that holds a <c>seats</c> folder, or null when there is none.</summary>
    /// <param name="reviewFolder">The review folder.</param>
    string? LatestCycle(string reviewFolder);
}
