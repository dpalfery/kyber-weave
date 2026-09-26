using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Snapshots a directory tree by normalized relative path and byte content so a test can
/// prove an operation left the tree untouched — including the files outside any destination
/// the operation was permitted to write. Paths are normalized to forward slashes so the
/// snapshot is comparable across platforms.
/// </summary>
internal static class DirectoryTreeSnapshot
{
    public static IReadOnlyDictionary<string, byte[]> SnapshotTree(string rootPath) =>
        Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    public static void AssertTreeUnchanged(string rootPath, IReadOnlyDictionary<string, byte[]> before)
    {
        IReadOnlyDictionary<string, byte[]> after = SnapshotTree(rootPath);
        Assert.Equal(before.Keys, after.Keys);
        foreach (string path in before.Keys)
            Assert.Equal(before[path], after[path]);
    }
}
