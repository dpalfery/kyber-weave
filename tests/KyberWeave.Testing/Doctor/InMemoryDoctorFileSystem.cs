using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Kernel.FileSystem;

namespace KyberWeave.Tests.Doctor;

/// <summary>The doctor's read seam over an <see cref="IFileSystem"/>, so a probe reads what a command wrote through the write seam.</summary>
/// <remarks>
/// <see cref="FakeDoctorFileSystem"/> holds its own entries and cannot see a file another seam wrote. A test that
/// installs and then inspects, or installs twice, hands this adapter and the writer the same in-memory file system.
/// </remarks>
public sealed class InMemoryDoctorFileSystem(IFileSystem files) : IDoctorFileSystem
{
    /// <inheritdoc />
    public bool FileExists(string path) => files.FileExists(path) || files.GetLinkTarget(path) is not null && !files.DirectoryExists(path);

    /// <inheritdoc />
    public bool IsPresentFile(string path) => files.FileExists(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) =>
        files.FileExists(path) ? files.ReadAllBytes(path) : throw new FileNotFoundException("No such file in the in-memory file system.", path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => files.DirectoryExists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));

    /// <inheritdoc />
    public IReadOnlyList<DoctorDirectoryEntry> ListEntries(string directory) =>
        files.DirectoryExists(directory)
            ? [.. files.EnumerateEntries(directory).Select(entry =>
                new DoctorDirectoryEntry(Path.GetFileName(entry.Path), entry.Path, entry.IsDirectory, entry.IsLink))]
            : [];

    /// <inheritdoc />
    public string ReadTail(string path, long maxBytes, out bool firstLineIsPartial)
    {
        byte[] bytes = ReadAllBytes(path);
        bool cut = bytes.Length > maxBytes;
        int start = cut ? (int)(bytes.Length - maxBytes) : 0;
        firstLineIsPartial = cut && bytes[start - 1] != (byte)'\n';
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}
