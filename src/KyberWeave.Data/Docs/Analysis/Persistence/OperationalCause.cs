namespace KyberWeave.Core.Docs.Analysis.Persistence;

/// <summary>Why an operational analysis-cache failure happened, which decides the remedy it reports.</summary>
internal enum OperationalCause
{
    /// <summary>The file or folder cannot be opened, read or written.</summary>
    Access,

    /// <summary>The volume is full.</summary>
    DiskFull,

    /// <summary>The disk reported an I/O error.</summary>
    DiskIo,

    /// <summary>The operation was interrupted.</summary>
    Interrupted,
}
