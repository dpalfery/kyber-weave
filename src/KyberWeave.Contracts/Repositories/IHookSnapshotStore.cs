namespace KyberWeave.Contracts.Repositories;

/// <summary>The pre-dispatch snapshot documents under an arbiter directory, written aside and moved into place under a lock.</summary>
public interface IHookSnapshotStore
{
    /// <summary>Whether the snapshot document exists.</summary>
    /// <param name="directory">The arbiter directory.</param>
    /// <param name="fileName">The document's file name.</param>
    bool Exists(string directory, string fileName);

    /// <summary>Reads the document without locking: atomic renames mean a reader only ever sees a whole document.</summary>
    /// <param name="directory">The arbiter directory.</param>
    /// <param name="fileName">The document's file name.</param>
    /// <returns>The text; empty when the document is missing or unreadable.</returns>
    string ReadText(string directory, string fileName);

    /// <summary>Rewrites the document under the store's lock. Persistence is best effort: a failure leaves the document as it was.</summary>
    /// <param name="directory">The arbiter directory.</param>
    /// <param name="fileName">The document's file name.</param>
    /// <param name="mutate">Takes the document's current text and returns the new text, or null to leave the document alone.</param>
    void Update(string directory, string fileName, Func<string, string?> mutate);

    /// <summary>Rewrites the document under the store's lock, waiting asynchronously for the lock. Persistence is best effort: a failure leaves the document as it was.</summary>
    /// <param name="directory">The arbiter directory.</param>
    /// <param name="fileName">The document's file name.</param>
    /// <param name="mutate">Takes the document's current text and returns the new text, or null to leave the document alone.</param>
    /// <param name="cancellationToken">Cancels the lock wait.</param>
    Task UpdateAsync(string directory, string fileName, Func<string, string?> mutate, CancellationToken cancellationToken);
}
