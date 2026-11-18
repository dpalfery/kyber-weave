using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Docs.FileSystem;
using KyberWeave.Data.Hooks;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>
/// A role view over the file-system family must agree with the family itself. The repository store exposes entry
/// listing twice — as <see cref="IRepositoryFileStore"/> and as <see cref="IFileSystem"/> — and a caller that holds one
/// must not see a different answer from a caller that holds the other.
/// </summary>
public sealed class RepositoryFileStoreViewTests
{
    [Fact]
    public void BothViewsListTheSameEntriesAndTolerateAMissingDirectory()
    {
        PhysicalFileSystem disk = new();
        string root = disk.CreateTemporaryDirectory("kw-store-view-");
        try
        {
            string target = Path.Combine(root, "target");
            disk.CreateDirectory(target);
            disk.CreateDirectoryLink(Path.Combine(root, "folder-link"), target);
            disk.WriteAllText(Path.Combine(root, "file.txt"), "x");

            PhysicalRepositoryFileStore store = new();
            IFileSystem readView = store;
            string missing = Path.Combine(root, "missing");

            Dictionary<string, FileSystemEntry> wide = readView
                .EnumerateEntries(root)
                .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            RepositoryEntry[] narrow = [.. store.EnumerateEntries(root)];

            Assert.NotEmpty(wide);
            Assert.Equal(wide.Count, narrow.Length);
            foreach (RepositoryEntry entry in narrow)
            {
                Assert.True(wide.TryGetValue(entry.Path, out FileSystemEntry expected), entry.Path);
                Assert.Equal(expected.IsDirectory, entry.IsDirectory);
                Assert.Equal(expected.IsLink, entry.IsLink);
            }

            Assert.Empty(readView.EnumerateEntries(missing));
            Assert.Empty(store.EnumerateEntries(missing));
        }
        finally
        {
            disk.DeleteTreeNoFollow(root);
        }
    }
}
