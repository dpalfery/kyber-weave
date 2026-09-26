using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="DocsRootPath.EnumerateContainedFiles"/> never descends into symbolic links,
/// whether they escape the root or point within it, ensuring no symlink encountered during
/// a walk causes an access attempt to a path outside the directory being listed.
/// </summary>
public sealed class DocsRootPathSymlinkContainmentTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly TempDirectory _outside = new();

    public void Dispose()
    {
        _root.Dispose();
        _outside.Dispose();
    }

    /// <summary>
    /// A directory entry under root that is a symlink to the outside temp directory is not
    /// descended into, so files in the outside directory never appear in the result.
    /// </summary>
    [Fact]
    public void DirectorySymlinkToOutsideRootIsNotDescended()
    {
        // Arrange: Create a structure where root contains a symlink to an outside directory
        string insideDir = Path.Combine(_root.Path, "inside");
        Directory.CreateDirectory(insideDir);
        string insideMarkdownFile = Path.Combine(insideDir, "inside.md");
        File.WriteAllText(insideMarkdownFile, "# Inside");

        // Create a file in the outside directory
        string outsideMarkdownFile = Path.Combine(_outside.Path, "outside.md");
        File.WriteAllText(outsideMarkdownFile, "# Outside");

        // Create a symlink in root pointing to the outside directory
        string symlinkPath = Path.Combine(_root.Path, "linked-outside");
        Directory.CreateSymbolicLink(symlinkPath, _outside.Path);

        // Act
        IEnumerable<string> files = DocsRootPath.EnumerateContainedFiles(_root.Path, "*.md");

        // Assert
        List<string> fileList = files.ToList();
        Assert.Single(fileList);
        Assert.Equal(insideMarkdownFile, fileList[0]);
        Assert.DoesNotContain(outsideMarkdownFile, fileList);
    }

    /// <summary>
    /// A file entry under root that is a symlink to a file in the outside directory does
    /// not appear in the result.
    /// </summary>
    [Fact]
    public void FileSymlinkToOutsideRootDoesNotAppear()
    {
        // Arrange: Create a real file outside
        string outsideMarkdownFile = Path.Combine(_outside.Path, "outside.md");
        File.WriteAllText(outsideMarkdownFile, "# Outside");

        // Create a regular markdown file inside
        string insideMarkdownFile = Path.Combine(_root.Path, "inside.md");
        File.WriteAllText(insideMarkdownFile, "# Inside");

        // Create a file symlink inside root pointing to the outside file
        string symlinkFile = Path.Combine(_root.Path, "linked-file.md");
        File.CreateSymbolicLink(symlinkFile, outsideMarkdownFile);

        // Act
        IEnumerable<string> files = DocsRootPath.EnumerateContainedFiles(_root.Path, "*.md");

        // Assert
        List<string> fileList = files.ToList();
        Assert.Single(fileList);
        Assert.Equal(insideMarkdownFile, fileList[0]);
        Assert.DoesNotContain(symlinkFile, fileList);
    }

    /// <summary>
    /// A symlink whose target happens to sit inside root itself is still skipped, because
    /// the containment policy never resolves a link — it uniformly skips all symlinks,
    /// whether they escape the root or point within it.
    /// </summary>
    [Fact]
    public void SymlinkToTargetInsideRootIsStillSkipped()
    {
        // Arrange: Create a real file inside root
        string subdir = Path.Combine(_root.Path, "subdir");
        Directory.CreateDirectory(subdir);
        string targetFile = Path.Combine(subdir, "target.md");
        File.WriteAllText(targetFile, "# Target");

        // Create another file in root
        string insideMarkdownFile = Path.Combine(_root.Path, "inside.md");
        File.WriteAllText(insideMarkdownFile, "# Inside");

        // Create a symlink inside root pointing to the target file
        string symlinkFile = Path.Combine(_root.Path, "linked-target.md");
        File.CreateSymbolicLink(symlinkFile, targetFile);

        // Act
        IEnumerable<string> files = DocsRootPath.EnumerateContainedFiles(_root.Path, "*.md");

        // Assert
        List<string> fileList = files.ToList();
        Assert.Equal(2, fileList.Count);
        Assert.Contains(insideMarkdownFile, fileList);
        Assert.Contains(targetFile, fileList);
        Assert.DoesNotContain(symlinkFile, fileList);
    }

    /// <summary>
    /// An ordinary markdown file several directories deep, with no symlink in its ancestry,
    /// is still returned. This guards against over-broad exclusion of regular files.
    /// </summary>
    [Fact]
    public void OrdinaryNestedMarkdownFileIsReturned()
    {
        // Arrange: Create a deeply nested file structure with no symlinks
        string deepPath = Path.Combine(_root.Path, "a", "b", "c", "d");
        Directory.CreateDirectory(deepPath);
        string nestedFile = Path.Combine(deepPath, "nested.md");
        File.WriteAllText(nestedFile, "# Nested");

        string rootFile = Path.Combine(_root.Path, "root.md");
        File.WriteAllText(rootFile, "# Root");

        // Create a symlink to the outside directory to ensure it's skipped
        Directory.CreateSymbolicLink(
            Path.Combine(_root.Path, "outside-link"),
            _outside.Path);

        // Act
        IEnumerable<string> files = DocsRootPath.EnumerateContainedFiles(_root.Path, "*.md");

        // Assert
        List<string> fileList = files.ToList();
        Assert.Equal(2, fileList.Count);
        Assert.Contains(rootFile, fileList);
        Assert.Contains(nestedFile, fileList);
    }

    /// <summary>
    /// Dot-prefixed directories and files are walked like any other entry. Unix marks them
    /// <see cref="FileAttributes.Hidden"/>, and the recursive overload this walk replaced
    /// skipped no attributes, so skipping them here would drop <c>.github/</c> and
    /// <c>.kyber-weave/</c> from a root of <c>.</c> without containing anything.
    /// </summary>
    [Fact]
    public void DotPrefixedEntriesAreWalked()
    {
        string dotDirectory = Path.Combine(_root.Path, ".github");
        Directory.CreateDirectory(dotDirectory);
        string nestedFile = Path.Combine(dotDirectory, "nested.md");
        File.WriteAllText(nestedFile, "# Nested");

        string dotFile = Path.Combine(_root.Path, ".notes.md");
        File.WriteAllText(dotFile, "# Notes");

        List<string> files = DocsRootPath.EnumerateContainedFiles(_root.Path, "*.md").ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains(nestedFile, files);
        Assert.Contains(dotFile, files);
    }
}
