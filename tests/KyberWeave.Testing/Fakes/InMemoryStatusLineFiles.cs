using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The status-line deployment's file role over an <see cref="InMemoryFileSystem"/>: the same reads, writes, link probe
/// and execute-bit bookkeeping as the on-disk adapter, held in memory so a deployment test needs no disk.
/// </summary>
/// <param name="files">The in-memory file system the deployment lives in.</param>
public sealed class InMemoryStatusLineFiles(InMemoryFileSystem files) : IStatusLineFiles
{
    private readonly HashSet<string> _executables = new(files.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool FileExists(string path) => files.FileExists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => files.DirectoryExists(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => files.ReadAllBytes(path);

    /// <inheritdoc />
    public string? ReadTextOrNull(string path) => files.FileExists(path) ? files.ReadAllText(path) : null;

    /// <inheritdoc />
    public void CreateDirectory(string path) => files.CreateDirectory(path);

    /// <inheritdoc />
    public void WriteAllBytes(string path, ReadOnlySpan<byte> content) => files.WriteAllBytes(path, content.ToArray());

    /// <inheritdoc />
    public void WriteTextAtomically(string path, string text) => files.WriteAllTextAtomic(path, text);

    /// <inheritdoc />
    public void DeleteFile(string path) => files.DeleteFile(path);

    /// <inheritdoc />
    public bool IsDirectoryEmpty(string path) => !files.EnumerateEntries(path).Any();

    /// <inheritdoc />
    public void DeleteDirectory(string path) => files.DeleteEmptyDirectory(path);

    /// <inheritdoc />
    public bool IsLinkOrReparsePoint(string path)
    {
        try
        {
            return files.GetLinkTarget(path) is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool HasUserExecuteBit(string path) => _executables.Contains(path);

    /// <inheritdoc />
    public void MarkExecutable(string path) => _executables.Add(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateRegularFiles(string directory) =>
        files.EnumerateFiles(directory, "*", recursive: true, followLinks: false);
}
