using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Doctor;

/// <summary>The git hook files over an <see cref="IFileSystem"/>, so a test reads back the hook a command wrote without a disk.</summary>
/// <param name="files">The in-memory file system the hooks live in.</param>
public sealed class InMemoryGitHookFiles(IWritableFileSystem files) : IGitHookFiles
{
    /// <inheritdoc />
    public bool Exists(string path) => files.FileExists(path);

    /// <inheritdoc />
    public bool IsExecutable(string path) => true;

    /// <inheritdoc />
    public string ReadAllText(string path) => files.ReadAllText(path);

    /// <inheritdoc />
    public void Write(string path, string text, bool makeExecutable)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            files.CreateDirectory(directory);
        }

        files.WriteAllText(path, text);
    }

    /// <inheritdoc />
    public void Delete(string path) => files.DeleteFile(path);
}
