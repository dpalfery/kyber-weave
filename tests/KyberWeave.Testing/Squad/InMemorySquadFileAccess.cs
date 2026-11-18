using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Squad;

/// <summary>The Squad lifecycle's file probes over an in-memory file system, with no checkout above the running binary.</summary>
/// <param name="files">The in-memory file system the probes read and write.</param>
public sealed class InMemorySquadFileAccess(InMemoryFileSystem files) : ISquadFileAccess
{
    private int _scratch;

    /// <inheritdoc />
    public bool FileExists(string path) => files.FileExists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => files.DirectoryExists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => files.ReadAllText(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => files.ReadAllBytes(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => files.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path) => files.DeleteTreeNoFollow(path);

    /// <inheritdoc />
    public string NewScratchDirectoryPath(string prefix) =>
        Path.Combine(files.Root, "scratch", prefix + Interlocked.Increment(ref _scratch).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory) =>
        files.EnumerateDirectories(directory)
            .Where(path => files.GetLinkTarget(path) is null)
            .Select(path => new SquadDirectoryEntry(Path.GetFileName(path), path));

    /// <inheritdoc />
    public string? FindCheckoutSquadSourceRoot() => null;
}
