using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Holds the cross-process lease on one deployment root, and the journal open, for the length of a transaction.</summary>
/// <param name="lease">The lease the lease seam granted; released when this one is disposed.</param>
/// <param name="files">The file system the journal is held open through.</param>
public sealed class TransactionLease(IDisposable lease, IWritableFileSystem files) : IDisposable
{
    private bool _disposed;
    private Stream? _journal;

    /// <summary>Releases the journal, then the lease.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            ReleaseJournal();
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Opens the journal so nothing else can change or delete it while the transaction runs.</summary>
    /// <param name="journalPath">The journal file.</param>
    public void HoldJournal(string journalPath)
    {
        _journal = files.OpenGuarded(journalPath);
    }

    /// <summary>Lets go of the journal so a new generation can replace it.</summary>
    public void ReleaseJournal()
    {
        _journal?.Dispose();
        _journal = null;
    }
}
