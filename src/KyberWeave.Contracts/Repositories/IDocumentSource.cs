using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads governed documents from a repository checkout.</summary>
public interface IDocumentSource
{
    /// <summary>Loads the in-scope documentation corpus.</summary>
    /// <returns>The parsed document set.</returns>
    DocumentSet Load();

    /// <summary>Reads one archived entry, which the corpus walk skips.</summary>
    /// <param name="relativePath">The entry's repository-relative path.</param>
    /// <returns>The parsed document.</returns>
    DocumentModel LoadArchivedDocument(string relativePath);
}
