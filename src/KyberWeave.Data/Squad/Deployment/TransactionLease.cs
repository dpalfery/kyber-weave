namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Holds the cross-process lease on one deployment root for the length of a transaction.</summary>
public sealed class TransactionLease(string identity, Mutex mutex) : IDisposable
{
    private bool _disposed;
    private FileStream? _journal;

    public void Dispose()
    {
        if (_disposed)
            return;

        ReleaseJournal();
        mutex.ReleaseMutex();
        mutex.Dispose();
        SquadTransaction.ActiveLeases.TryRemove(identity, out _);
        _disposed = true;
    }

    public void HoldJournal(string journalPath)
    {
        _journal = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
    }

    public void ReleaseJournal()
    {
        _journal?.Dispose();
        _journal = null;
    }
}
