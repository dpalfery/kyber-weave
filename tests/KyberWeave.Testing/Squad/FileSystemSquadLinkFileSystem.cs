using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The Squad link file system over the file-system seam, so every link it creates, tests or removes is one counted
/// operation of whatever file system it is built on: the fake, or a fault-injecting wrapper over the real disk.
/// </summary>
/// <param name="files">The file system the links live in.</param>
public sealed class FileSystemSquadLinkFileSystem(IWritableFileSystem files) : ISquadLinkFileSystem
{
    /// <inheritdoc />
    public void CreateFileLink(string linkPath, string targetPath) => files.CreateFileLink(linkPath, targetPath);

    /// <inheritdoc />
    public void CreateFolderLink(string linkPath, string targetPath) => files.CreateDirectoryLink(linkPath, targetPath);

    /// <inheritdoc />
    public bool IsLink(string path) => files.GetLinkTarget(path) is not null;

    /// <inheritdoc />
    public bool IsLinkTo(string linkPath, string expectedTarget) =>
        files.GetLinkTarget(linkPath) is { } target &&
        string.Equals(
            target.TrimEnd('/', '\\'),
            expectedTarget.TrimEnd('/', '\\'),
            StringComparison.Ordinal);

    /// <inheritdoc />
    public void DeleteTreeNoFollow(string directoryPath) => files.DeleteTreeNoFollow(directoryPath);
}
