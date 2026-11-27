namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>Which ledger events belong to an <see cref="ArbiterRunScope"/>.</summary>
public static class ArbiterRunScopeQueries
{
    /// <summary>
    /// Whether a pre event belongs to this run: it was recorded after the run started and names this run's
    /// directory. An event that names no run (written before the field existed, or evaluated outside any run)
    /// is placed by its time alone.
    /// </summary>
    /// <param name="scope">The run the event is judged within.</param>
    /// <param name="pre">The pre event.</param>
    public static bool Includes(this ArbiterRunScope scope, ArbiterLedgerEvent pre)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(pre);
        return (scope.StartedAt is null || pre.At >= scope.StartedAt)
            && (pre.Run is null || string.Equals(Normalize(pre.Run), Normalize(scope.Root), StringComparison.Ordinal));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
