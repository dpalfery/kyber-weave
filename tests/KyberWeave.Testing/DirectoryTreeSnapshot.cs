using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Snapshots a directory tree by normalized relative path and byte content so a test can
/// prove an operation left the tree untouched — including the files outside any destination
/// the operation was permitted to write. Paths are normalized to forward slashes so the
/// snapshot is comparable across platforms. Empty directories are recorded alongside the
/// files because a file-only snapshot cannot see a directory being created or removed when
/// nothing inside it changes — a rejection path that creates an empty directory before
/// failing would otherwise look like "untouched".
/// </summary>
internal static class DirectoryTreeSnapshot
{
    public static TreeSnapshot SnapshotTree(string rootPath)
    {
        IReadOnlyDictionary<string, byte[]> files = Directory
            .EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => NormalizePath(rootPath, path),
                File.ReadAllBytes,
                StringComparer.Ordinal);
        SortedSet<string> directories = new(
            Directory
                .EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
                .Select(path => NormalizePath(rootPath, path)),
            StringComparer.Ordinal);
        return new TreeSnapshot(files, directories);
    }

    public static void AssertTreeUnchanged(string rootPath, IReadOnlyDictionary<string, byte[]> before)
    {
        TreeSnapshot after = SnapshotTree(rootPath);
        IReadOnlySet<string>? beforeDirectories = (before as TreeSnapshot)?.Directories;

        foreach (string missingFile in before.Keys.Except(after.Files.Keys, StringComparer.Ordinal))
            Assert.Fail($"Expected file '{missingFile}' to still exist, but it is gone.");
        foreach (string unexpectedFile in after.Files.Keys.Except(before.Keys, StringComparer.Ordinal))
            Assert.Fail($"Found unexpected file '{unexpectedFile}'.");
        foreach (string path in before.Keys)
            Assert.Equal(before[path], after.Files[path]);

        if (beforeDirectories is null)
            return;

        foreach (string missingDirectory in beforeDirectories.Except(after.Directories, StringComparer.Ordinal))
            Assert.Fail($"Expected directory '{missingDirectory}' to still exist, but it is gone.");
        foreach (string unexpectedDirectory in after.Directories.Except(beforeDirectories, StringComparer.Ordinal))
            Assert.Fail($"Found unexpected directory '{unexpectedDirectory}'.");
    }

    private static string NormalizePath(string rootPath, string path) =>
        Path.GetRelativePath(rootPath, path).Replace('\\', '/');
}

/// <summary>
/// The snapshot payload: byte content by normalized relative file path, plus the normalized
/// relative directory paths. It implements the file dictionary so the call sites that hold a
/// snapshot as <see cref="IReadOnlyDictionary{TKey, TValue}"/> compile unchanged, while
/// <see cref="DirectoryTreeSnapshot.AssertTreeUnchanged"/> can recognize its own snapshots
/// and compare the directories as well.
/// </summary>
internal sealed class TreeSnapshot(IReadOnlyDictionary<string, byte[]> files, IReadOnlySet<string> directories)
    : IReadOnlyDictionary<string, byte[]>
{
    public IReadOnlyDictionary<string, byte[]> Files { get; } = files;

    public IReadOnlySet<string> Directories { get; } = directories;

    public int Count => Files.Count;

    public IEnumerable<string> Keys => Files.Keys;

    public IEnumerable<byte[]> Values => Files.Values;

    public byte[] this[string key] => Files[key];

    public bool ContainsKey(string key) => Files.ContainsKey(key);

    public bool TryGetValue(string key, out byte[] value) => Files.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator() => Files.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        ((System.Collections.IEnumerable)Files).GetEnumerator();
}
