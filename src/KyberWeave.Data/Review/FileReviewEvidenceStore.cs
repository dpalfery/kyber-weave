using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Writes a review cycle's evidence files on the file system.</summary>
public sealed class FileReviewEvidenceStore(IWritableFileSystem files) : IReviewEvidenceStore
{
    /// <inheritdoc />
    public void EnsureFolder(string folder) => files.CreateDirectory(folder);

    /// <inheritdoc />
    public void WriteAtomically(string path, string text)
    {
        string temporary = path + ".tmp";
        files.WriteAllText(temporary, text);
        files.MoveFile(temporary, path, overwrite: true);
    }
}
