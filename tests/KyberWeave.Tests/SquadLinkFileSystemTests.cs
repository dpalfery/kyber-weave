using System.Text;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit tests for <see cref="ISquadLinkFileSystem"/> port and its platform implementations
/// (<see cref="UnixSquadLinkFileSystem"/> and <see cref="WindowsSquadLinkFileSystem"/>).
/// </summary>
/// <remarks>
/// <para>
/// Acceptance criterion 7.5: Squad SHALL support instances on macOS and Windows. On Windows,
/// creating links SHALL NOT need administrator rights or Developer Mode.
/// </para>
/// <para>
/// Acceptance criterion 7.8: WHEN Squad removes or refreshes an instance THEN it SHALL delete
/// only the instance's own files and links. It SHALL NOT delete or change any link's target.
/// </para>
/// </remarks>
public sealed class SquadLinkFileSystemTests : IDisposable
{
    private readonly TempDirectory _tempDirectory = new();

    public void Dispose()
    {
        _tempDirectory.Dispose();
    }

    [Fact]
    public void SquadLinkExceptionPreservesMessageAndPathProperties()
    {
        const string message = "Failed to create link";
        const string linkPath = "/workspace/instance/agent.md";
        const string targetPath = "/repo/agents/agent.md";

        SquadLinkException exception = new(message, linkPath, targetPath);

        Assert.Equal(message, exception.Message);
        Assert.Equal(linkPath, exception.LinkPath);
        Assert.Equal(targetPath, exception.TargetPath);
        Assert.IsAssignableFrom<Exception>(exception);
    }

    [Fact]
    public void UnixSquadLinkFileSystemCreatesFileAndFolderLinksOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix squad link filesystem tests run on macOS and Linux CI.");
        }

        ISquadLinkFileSystem fileSystem = new UnixSquadLinkFileSystem();

        string externalDir = Path.Combine(_tempDirectory.Path, "external-target-dir");
        Directory.CreateDirectory(externalDir);
        string externalFile = Path.Combine(externalDir, "source.txt");
        File.WriteAllText(externalFile, "canonical-file-content", Encoding.UTF8);

        string instanceDir = Path.Combine(_tempDirectory.Path, "instance");
        Directory.CreateDirectory(instanceDir);

        string folderLink = Path.Combine(instanceDir, "linked-dir");
        string fileLink = Path.Combine(instanceDir, "linked-file.txt");

        fileSystem.CreateFolderLink(folderLink, externalDir);
        fileSystem.CreateFileLink(fileLink, externalFile);

        Assert.True(fileSystem.IsLink(folderLink), $"Expected '{folderLink}' to be recognized as a link.");
        Assert.True(fileSystem.IsLink(fileLink), $"Expected '{fileLink}' to be recognized as a link.");
        Assert.True(fileSystem.IsLinkTo(folderLink, externalDir), $"Expected '{folderLink}' to link to '{externalDir}'.");
        Assert.True(fileSystem.IsLinkTo(fileLink, externalFile), $"Expected '{fileLink}' to link to '{externalFile}'.");

        Assert.True(File.Exists(fileLink));
        Assert.Equal("canonical-file-content", File.ReadAllText(fileLink, Encoding.UTF8));

        string resolvedFileViaFolderLink = Path.Combine(folderLink, "source.txt");
        Assert.True(File.Exists(resolvedFileViaFolderLink));
        Assert.Equal("canonical-file-content", File.ReadAllText(resolvedFileViaFolderLink, Encoding.UTF8));
    }

    [Fact]
    public void UnixSquadLinkFileSystemIsLinkReturnsFalseForRegularEntries()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix squad link filesystem tests run on macOS and Linux CI.");
        }

        ISquadLinkFileSystem fileSystem = new UnixSquadLinkFileSystem();

        string regularDir = Path.Combine(_tempDirectory.Path, "regular-dir");
        Directory.CreateDirectory(regularDir);
        string regularFile = Path.Combine(regularDir, "plain.txt");
        File.WriteAllText(regularFile, "not-a-link", Encoding.UTF8);

        Assert.False(fileSystem.IsLink(regularDir), "Regular directory must not be identified as a link.");
        Assert.False(fileSystem.IsLink(regularFile), "Regular file must not be identified as a link.");
        Assert.False(fileSystem.IsLink(Path.Combine(_tempDirectory.Path, "nonexistent")), "Nonexistent path must not be identified as a link.");
    }

    [Fact]
    public void UnixSquadLinkFileSystemIsLinkToReturnsFalseWhenTargetDiffersOrLinkOverwritten()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix squad link filesystem tests run on macOS and Linux CI.");
        }

        ISquadLinkFileSystem fileSystem = new UnixSquadLinkFileSystem();

        string targetDir = Path.Combine(_tempDirectory.Path, "target-dir");
        Directory.CreateDirectory(targetDir);
        string targetFile = Path.Combine(targetDir, "original.txt");
        File.WriteAllText(targetFile, "original", Encoding.UTF8);

        string otherFile = Path.Combine(targetDir, "other.txt");
        File.WriteAllText(otherFile, "other", Encoding.UTF8);

        string linkPath = Path.Combine(_tempDirectory.Path, "test-link.txt");
        fileSystem.CreateFileLink(linkPath, targetFile);

        Assert.True(fileSystem.IsLinkTo(linkPath, targetFile));
        Assert.False(fileSystem.IsLinkTo(linkPath, otherFile), "IsLinkTo must return false for a different target.");

        File.Delete(linkPath);
        File.WriteAllText(linkPath, "regular-file-replacing-link", Encoding.UTF8);

        Assert.False(fileSystem.IsLink(linkPath), "Overwritten regular file must not be identified as a link.");
        Assert.False(fileSystem.IsLinkTo(linkPath, targetFile), "Overwritten regular file must not link to target.");
    }

    [Fact]
    public void UnixSquadLinkFileSystemDeleteTreeNoFollowPreservesTargetDirectoryAndFileContents()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix squad link filesystem tests run on macOS and Linux CI.");
        }

        ISquadLinkFileSystem fileSystem = new UnixSquadLinkFileSystem();

        string externalTargetDir = Path.Combine(_tempDirectory.Path, "external-target");
        Directory.CreateDirectory(externalTargetDir);
        string externalTargetFile = Path.Combine(externalTargetDir, "preserved.txt");
        const string expectedTargetBytes = "immutable-target-content-must-survive";
        File.WriteAllText(externalTargetFile, expectedTargetBytes, Encoding.UTF8);

        string externalLoneFile = Path.Combine(_tempDirectory.Path, "standalone.txt");
        File.WriteAllText(externalLoneFile, "standalone-bytes", Encoding.UTF8);

        string instanceDir = Path.Combine(_tempDirectory.Path, "instance-root");
        Directory.CreateDirectory(instanceDir);

        string innerRealDir = Path.Combine(instanceDir, "real-nested-folder");
        Directory.CreateDirectory(innerRealDir);
        string innerRealFile = Path.Combine(innerRealDir, "ephemeral.txt");
        File.WriteAllText(innerRealFile, "ephemeral-instance-state", Encoding.UTF8);

        string folderLink = Path.Combine(instanceDir, "linked-target-dir");
        fileSystem.CreateFolderLink(folderLink, externalTargetDir);

        string fileLink = Path.Combine(instanceDir, "linked-target-file.txt");
        fileSystem.CreateFileLink(fileLink, externalLoneFile);

        fileSystem.DeleteTreeNoFollow(instanceDir);

        Assert.False(Directory.Exists(instanceDir), $"Instance directory '{instanceDir}' must be deleted.");
        Assert.False(Directory.Exists(innerRealDir), "Real nested directories inside the deleted tree must be removed.");
        Assert.False(File.Exists(innerRealFile), "Real files inside the deleted tree must be removed.");

        Assert.True(Directory.Exists(externalTargetDir), "External target directory must remain completely intact.");
        Assert.True(File.Exists(externalTargetFile), "Target file inside linked directory must remain completely intact.");
        Assert.Equal(expectedTargetBytes, File.ReadAllText(externalTargetFile, Encoding.UTF8));

        Assert.True(File.Exists(externalLoneFile), "External standalone linked file must remain completely intact.");
        Assert.Equal("standalone-bytes", File.ReadAllText(externalLoneFile, Encoding.UTF8));
    }

    [Fact]
    public void WindowsSquadLinkFileSystemCreatesJunctionAndFileLinksOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows squad link filesystem tests run on Windows.");
        }

        ISquadLinkFileSystem fileSystem = new WindowsSquadLinkFileSystem();

        string externalDir = Path.Combine(_tempDirectory.Path, "win-external-dir");
        Directory.CreateDirectory(externalDir);
        string externalFile = Path.Combine(externalDir, "win-source.txt");
        File.WriteAllText(externalFile, "win-canonical-content", Encoding.UTF8);

        string instanceDir = Path.Combine(_tempDirectory.Path, "win-instance");
        Directory.CreateDirectory(instanceDir);

        string folderLink = Path.Combine(instanceDir, "win-junction-dir");
        string fileLink = Path.Combine(instanceDir, "win-hardlink-file.txt");

        fileSystem.CreateFolderLink(folderLink, externalDir);
        fileSystem.CreateFileLink(fileLink, externalFile);

        Assert.True(fileSystem.IsLink(folderLink), $"Expected junction '{folderLink}' to be recognized as a link.");
        Assert.True(fileSystem.IsLink(fileLink), $"Expected file link '{fileLink}' to be recognized as a link.");
        Assert.True(fileSystem.IsLinkTo(folderLink, externalDir), $"Expected '{folderLink}' to link to '{externalDir}'.");
        Assert.True(fileSystem.IsLinkTo(fileLink, externalFile), $"Expected '{fileLink}' to link to '{externalFile}'.");

        Assert.True(File.Exists(fileLink));
        Assert.Equal("win-canonical-content", File.ReadAllText(fileLink, Encoding.UTF8));
    }

    [Fact]
    public void WindowsSquadLinkFileSystemDeleteTreeNoFollowRemovesLinksWithoutFollowingIntoTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows squad link filesystem tests run on Windows.");
        }

        ISquadLinkFileSystem fileSystem = new WindowsSquadLinkFileSystem();

        string externalDir = Path.Combine(_tempDirectory.Path, "win-preserve-target");
        Directory.CreateDirectory(externalDir);
        string targetFile = Path.Combine(externalDir, "target-payload.txt");
        const string payload = "windows-target-payload-must-survive";
        File.WriteAllText(targetFile, payload, Encoding.UTF8);

        string instanceDir = Path.Combine(_tempDirectory.Path, "win-instance-root");
        Directory.CreateDirectory(instanceDir);

        string folderLink = Path.Combine(instanceDir, "junction-to-target");
        fileSystem.CreateFolderLink(folderLink, externalDir);

        fileSystem.DeleteTreeNoFollow(instanceDir);

        Assert.False(Directory.Exists(instanceDir), $"Instance directory '{instanceDir}' must be removed.");
        Assert.True(Directory.Exists(externalDir), "External target directory must remain completely intact.");
        Assert.True(File.Exists(targetFile), "External target file must remain completely intact.");
        Assert.Equal(payload, File.ReadAllText(targetFile, Encoding.UTF8));
    }

    [Fact]
    public void WindowsSquadLinkFileSystemThrowsSquadLinkExceptionOnInvalidTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows squad link filesystem tests run on Windows.");
        }

        ISquadLinkFileSystem fileSystem = new WindowsSquadLinkFileSystem();

        string linkPath = Path.Combine(_tempDirectory.Path, "invalid-link.txt");
        string nonExistentTarget = Path.Combine(_tempDirectory.Path, "does-not-exist.txt");

        SquadLinkException exception = Assert.Throws<SquadLinkException>(() =>
            fileSystem.CreateFileLink(linkPath, nonExistentTarget));

        Assert.Equal(linkPath, exception.LinkPath);
        Assert.Equal(nonExistentTarget, exception.TargetPath);
    }

    [Fact]
    public void PlatformSquadLinkFileSystemCreatesAndValidatesLinksForCurrentHost()
    {
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        string targetDir = Path.Combine(_tempDirectory.Path, "host-target-dir");
        Directory.CreateDirectory(targetDir);
        string targetFile = Path.Combine(targetDir, "host-target-file.txt");
        const string content = "host-platform-content";
        File.WriteAllText(targetFile, content, Encoding.UTF8);

        string instanceDir = Path.Combine(_tempDirectory.Path, "host-instance-dir");
        Directory.CreateDirectory(instanceDir);

        string folderLink = Path.Combine(instanceDir, "host-folder-link");
        string fileLink = Path.Combine(instanceDir, "host-file-link.txt");

        fileSystem.CreateFolderLink(folderLink, targetDir);
        fileSystem.CreateFileLink(fileLink, targetFile);

        Assert.True(fileSystem.IsLink(folderLink));
        Assert.True(fileSystem.IsLink(fileLink));
        Assert.True(fileSystem.IsLinkTo(folderLink, targetDir));
        Assert.True(fileSystem.IsLinkTo(fileLink, targetFile));

        Assert.Equal(content, File.ReadAllText(fileLink, Encoding.UTF8));

        fileSystem.DeleteTreeNoFollow(instanceDir);

        Assert.False(Directory.Exists(instanceDir));
        Assert.True(Directory.Exists(targetDir));
        Assert.True(File.Exists(targetFile));
        Assert.Equal(content, File.ReadAllText(targetFile, Encoding.UTF8));
    }

    [Fact]
    public void DeleteTreeNoFollowRecursivelyRemovesRealDirectoriesAndFilesInsideTree()
    {
        ISquadLinkFileSystem fileSystem = CreatePlatformFileSystem();

        string root = Path.Combine(_tempDirectory.Path, "tree-to-delete");
        string sub1 = Path.Combine(root, "level1");
        string sub2 = Path.Combine(sub1, "level2");
        Directory.CreateDirectory(sub2);

        string file1 = Path.Combine(root, "file1.txt");
        string file2 = Path.Combine(sub1, "file2.txt");
        string file3 = Path.Combine(sub2, "file3.txt");
        File.WriteAllText(file1, "1", Encoding.UTF8);
        File.WriteAllText(file2, "2", Encoding.UTF8);
        File.WriteAllText(file3, "3", Encoding.UTF8);

        fileSystem.DeleteTreeNoFollow(root);

        Assert.False(Directory.Exists(root), $"Tree root '{root}' must be removed.");
        Assert.False(Directory.Exists(sub1), $"Nested directory '{sub1}' must be removed.");
        Assert.False(Directory.Exists(sub2), $"Nested directory '{sub2}' must be removed.");
        Assert.False(File.Exists(file1), $"File '{file1}' must be removed.");
        Assert.False(File.Exists(file2), $"File '{file2}' must be removed.");
        Assert.False(File.Exists(file3), $"File '{file3}' must be removed.");
    }

    private static ISquadLinkFileSystem CreatePlatformFileSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSquadLinkFileSystem();
        }

        return new UnixSquadLinkFileSystem();
    }
}
