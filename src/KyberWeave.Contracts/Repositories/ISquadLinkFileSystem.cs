namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Filesystem operations for creating and inspecting links without administrator rights.
/// </summary>
public interface ISquadLinkFileSystem
{
    /// <summary>
    /// Creates a file link at <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>.
    /// </summary>
    /// <param name="linkPath">The path of the file link to create.</param>
    /// <param name="targetPath">The existing target file path.</param>
    void CreateFileLink(string linkPath, string targetPath);

    /// <summary>
    /// Creates a directory or junction link at <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>.
    /// </summary>
    /// <param name="linkPath">The path of the directory link to create.</param>
    /// <param name="targetPath">The existing target directory path.</param>
    void CreateFolderLink(string linkPath, string targetPath);

    /// <summary>
    /// Checks whether the filesystem entry at <paramref name="path"/> is a link (symbolic link, junction, or hard link).
    /// </summary>
    /// <param name="path">The filesystem path to test.</param>
    /// <returns><c>true</c> if the path exists and is a link; otherwise, <c>false</c>.</returns>
    bool IsLink(string path);

    /// <summary>
    /// Checks whether the link at <paramref name="linkPath"/> targets <paramref name="expectedTarget"/>.
    /// </summary>
    /// <param name="linkPath">The filesystem path of the link.</param>
    /// <param name="expectedTarget">The target path expected to match the link target.</param>
    /// <returns><c>true</c> if the link exists and points to <paramref name="expectedTarget"/>; otherwise, <c>false</c>.</returns>
    bool IsLinkTo(string linkPath, string expectedTarget);

    /// <summary>
    /// Recursively removes all files and subdirectories under <paramref name="directoryPath"/> without following
    /// links into external directories, and removes the root directory itself.
    /// </summary>
    /// <param name="directoryPath">The root directory of the tree to delete.</param>
    void DeleteTreeNoFollow(string directoryPath);
}
