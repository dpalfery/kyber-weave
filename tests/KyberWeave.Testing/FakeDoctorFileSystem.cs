using System.Text;
using KyberWeave.Kernel.FileSystem;

namespace KyberWeave.Tests;

/// <summary>An in-memory <see cref="IDoctorFileSystem"/>: a probe reads only what a test put there.</summary>
/// <remarks>
/// Paths are absolutized on the way in and out, so a caller that resolves a relative path reads the same entry.
/// It holds files and directories only; no test needs a link or a live process's log here.
/// </remarks>
internal sealed class FakeDoctorFileSystem : IDoctorFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

    /// <summary>Adds a text file, replacing any bytes already at that path.</summary>
    public FakeDoctorFileSystem WithFile(string path, string text) =>
        WithFile(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Adds a binary file, replacing any bytes already at that path.</summary>
    public FakeDoctorFileSystem WithFile(string path, byte[] bytes)
    {
        _files[Absolute(path)] = bytes;
        return this;
    }

    /// <summary>Adds an empty directory.</summary>
    public FakeDoctorFileSystem WithDirectory(string path)
    {
        _directories.Add(Absolute(path));
        return this;
    }

    /// <inheritdoc />
    public bool FileExists(string path) => _files.ContainsKey(Absolute(path));

    /// <inheritdoc />
    public bool IsPresentFile(string path) => FileExists(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) =>
        _files.TryGetValue(Absolute(path), out byte[]? bytes)
            ? bytes
            : throw new FileNotFoundException("No such file in the fake file system.", path);

    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        string full = Absolute(path);
        return _directories.Contains(full)
            || _files.Keys.Any(file => file.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));

    /// <inheritdoc />
    public IReadOnlyList<DoctorDirectoryEntry> ListEntries(string directory)
    {
        string full = Absolute(directory);
        List<DoctorDirectoryEntry> entries = [];
        foreach (string file in _files.Keys)
        {
            if (Path.GetDirectoryName(file) == full)
                entries.Add(new DoctorDirectoryEntry(Path.GetFileName(file), file, IsDirectory: false, IsLink: false));
        }

        foreach (string child in _directories)
        {
            if (Path.GetDirectoryName(child) == full)
                entries.Add(new DoctorDirectoryEntry(Path.GetFileName(child), child, IsDirectory: true, IsLink: false));
        }

        return entries;
    }

    /// <inheritdoc />
    public string ReadTail(string path, long maxBytes, out bool firstLineIsPartial)
    {
        byte[] bytes = ReadAllBytes(path);
        int take = (int)Math.Min(bytes.Length, Math.Max(0, maxBytes));
        int start = bytes.Length - take;
        firstLineIsPartial = start > 0 && bytes[start - 1] != (byte)'\n';
        return Encoding.UTF8.GetString(bytes, start, take);
    }

    private static string Absolute(string path) => Path.GetFullPath(path);
}
