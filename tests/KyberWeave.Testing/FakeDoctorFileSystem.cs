using System.Text;
using KyberWeave.Kernel.FileSystem;

namespace KyberWeave.Tests;

/// <summary>An in-memory <see cref="IDoctorFileSystem"/>: a probe reads only what a test put there.</summary>
/// <remarks>
/// <para>
/// Paths are absolutized on the way in and out, so a caller that resolves a relative path reads the same entry.
/// </para>
/// <para>
/// It models the real adapter's link rules: a link is never written, read or listed as itself. Existence resolves
/// the link, <see cref="IsPresentFile"/> answers true only when the link's final target is a file — the adapter asks
/// a <see cref="FileInfo"/> to resolve the link, so a directory target is not a present file — and
/// <see cref="ListEntries"/> flags the link. The contract suite pins the fake against the adapter.
/// </para>
/// </remarks>
internal sealed class FakeDoctorFileSystem : IDoctorFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);

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

    /// <summary>Adds a symbolic link to <paramref name="targetPath"/>, which need not exist.</summary>
    public FakeDoctorFileSystem WithLink(string path, string targetPath)
    {
        _links[Absolute(path)] = Absolute(targetPath);
        return this;
    }

    /// <inheritdoc />
    public bool FileExists(string path) => _files.ContainsKey(Resolve(path));

    /// <inheritdoc />
    public bool IsPresentFile(string path)
    {
        string full = Absolute(path);
        if (!_links.ContainsKey(full))
            return _files.ContainsKey(full);

        // The adapter asks a FileInfo to resolve the link and to say whether the final target is a file, so a link
        // to a directory has nothing to read and answers false.
        return _files.ContainsKey(Resolve(full));
    }

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) =>
        _files.TryGetValue(Resolve(path), out byte[]? bytes)
            ? bytes
            : throw new FileNotFoundException("No such file in the fake file system.", path);

    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        string full = Resolve(path);
        return _directories.Contains(full)
            || _files.Keys.Any(child => IsUnder(child, full))
            || _directories.Any(child => IsUnder(child, full))
            || _links.Keys.Any(child => IsUnder(child, full));
    }

    /// <inheritdoc />
    public string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));

    /// <inheritdoc />
    public IReadOnlyList<DoctorDirectoryEntry> ListEntries(string directory)
    {
        string full = Resolve(directory);
        List<DoctorDirectoryEntry> entries = [];
        foreach (string file in _files.Keys)
        {
            if (IsImmediateChild(file, full))
                entries.Add(new DoctorDirectoryEntry(Path.GetFileName(file), file, IsDirectory: false, IsLink: false));
        }

        foreach (string child in _directories)
        {
            if (IsImmediateChild(child, full))
                entries.Add(new DoctorDirectoryEntry(Path.GetFileName(child), child, IsDirectory: true, IsLink: false));
        }

        foreach (string link in _links.Keys)
        {
            if (IsImmediateChild(link, full))
            {
                entries.Add(new DoctorDirectoryEntry(
                    Path.GetFileName(link),
                    link,
                    IsDirectory: DirectoryExists(link),
                    IsLink: true));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public string ReadTail(string path, long maxBytes, out bool firstLineIsPartial)
    {
        // Mirrors the adapter: when the file is longer than the window it reads the last window, and the byte before
        // the window says whether the window starts part-way through a line.
        byte[] bytes = ReadAllBytes(path);
        bool cut = bytes.Length > maxBytes;
        int start = cut ? (int)(bytes.Length - maxBytes) : 0;
        firstLineIsPartial = cut && bytes[start - 1] != (byte)'\n';
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private string Resolve(string path)
    {
        string current = Absolute(path);
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (_links.TryGetValue(current, out string? target) && seen.Add(current))
            current = target;
        return current;
    }

    private static bool IsUnder(string child, string directory) =>
        child.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsImmediateChild(string child, string directory) =>
        Path.GetDirectoryName(child) == directory;

    private static string Absolute(string path) => Path.GetFullPath(path);
}
