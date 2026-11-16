namespace KyberWeave.Contracts.Repositories;

/// <summary>Writes the files of a review cycle's evidence.</summary>
public interface IReviewEvidenceStore
{
    /// <summary>Creates the cycle folder and its missing parents.</summary>
    /// <param name="folder">The cycle folder.</param>
    void EnsureFolder(string folder);

    /// <summary>Replaces a file in one rename, so a reader never sees half a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">The text.</param>
    void WriteAtomically(string path, string text);
}
