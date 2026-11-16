using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Writes a review cycle's evidence files on the file system.</summary>
public sealed class FileReviewEvidenceStore : IReviewEvidenceStore
{
    /// <inheritdoc />
    public void EnsureFolder(string folder) => _ = Directory.CreateDirectory(folder);

    /// <inheritdoc />
    public void WriteAtomically(string path, string text)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
