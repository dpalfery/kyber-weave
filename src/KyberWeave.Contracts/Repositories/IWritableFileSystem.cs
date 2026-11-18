namespace KyberWeave.Contracts.Repositories;

/// <summary>The file system writes and probes a command needs beyond the reads in <see cref="IFileSystem"/>.</summary>
/// <remarks>
/// Every write is documented with the failures it raises, because the callers that protect a user's files decide
/// what to do from the exception they catch; a silently swallowed failure loses a file instead of reporting it.
/// </remarks>
public interface IWritableFileSystem : IFileSystem
{
    /// <summary>Gets whether a file or a directory exists at a path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True when something exists there.</returns>
    bool PathExists(string path);

    /// <summary>Creates a directory and its missing parents.</summary>
    /// <param name="path">The directory.</param>
    /// <exception cref="UnauthorizedAccessException">The process may not create the directory.</exception>
    /// <exception cref="IOException">The directory could not be created, or a file is in its place.</exception>
    void CreateDirectory(string path);

    /// <summary>Writes a whole text file, replacing any earlier one.</summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The text.</param>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not write the file.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    void WriteAllText(string path, string contents);

    /// <summary>Writes a whole file as bytes, replacing any earlier one.</summary>
    /// <param name="path">The file.</param>
    /// <param name="bytes">The bytes.</param>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not write the file.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    void WriteAllBytes(string path, byte[] bytes);

    /// <summary>Writes a whole text file so a reader never sees it half written: a full temporary sibling is renamed over it.</summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="contents">The text.</param>
    /// <remarks>A directory named by the path is created first. A failed write leaves the earlier file in place and
    /// removes the temporary, best effort.</remarks>
    /// <exception cref="InvalidOperationException">The path has no parent directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not write the file or its directory.</exception>
    /// <exception cref="IOException">The write or the rename could not complete.</exception>
    void WriteAllTextAtomic(string path, string contents);

    /// <summary>Writes a whole file as bytes so a reader never sees it half written: a full temporary sibling is renamed over it.</summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="bytes">The complete bytes the file ends up holding.</param>
    /// <remarks>A directory named by the path is created first. A failed write leaves the earlier file in place and
    /// removes the temporary, best effort.</remarks>
    /// <exception cref="InvalidOperationException">The path has no parent directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not write the file or its directory.</exception>
    /// <exception cref="IOException">The write or the rename could not complete.</exception>
    void WriteAllBytesAtomic(string path, byte[] bytes);

    /// <summary>Moves a file to a path that must be free, as one rename.</summary>
    /// <param name="sourcePath">The file to move.</param>
    /// <param name="destinationPath">Where it goes; nothing may be there.</param>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="IOException">
    /// The destination exists, a directory is there, or the move crosses a volume boundary and cannot be one rename.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The process may not move the file.</exception>
    void MoveFile(string sourcePath, string destinationPath);

    /// <summary>Moves a file to a path, replacing what is there when asked, as one rename.</summary>
    /// <param name="sourcePath">The file to move.</param>
    /// <param name="destinationPath">Where it goes.</param>
    /// <param name="overwrite">True to replace an existing file; false to fail as the two-argument overload does.</param>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="IOException">
    /// <paramref name="overwrite"/> is false and the destination exists, or the move crosses a volume boundary and
    /// cannot be one rename.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The process may not move the file.</exception>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>Creates an empty file that must not exist yet, so only one caller can win a race for the path.</summary>
    /// <param name="path">The file.</param>
    /// <exception cref="IOException">The file already exists, or it could not be created.</exception>
    void CreateNewFile(string path);

    /// <summary>Creates an empty file only if nothing is there, reporting a lost race instead of throwing.</summary>
    /// <param name="path">The file.</param>
    /// <returns>True when this call created the file; false when a file or a directory already occupies the path.</returns>
    /// <remarks>A failure that is not a lost race, such as a full disk, still throws.</remarks>
    /// <exception cref="UnauthorizedAccessException">The process may not create the file.</exception>
    /// <exception cref="IOException">The file could not be created for a reason other than the path being taken.</exception>
    bool TryCreateNewFile(string path);

    /// <summary>Deletes a file. A missing file is not an error.</summary>
    /// <param name="path">The file.</param>
    /// <exception cref="UnauthorizedAccessException">The process may not delete the file, or a directory is in its place.</exception>
    /// <exception cref="IOException">The file could not be deleted, for example because it is open.</exception>
    void DeleteFile(string path);

    /// <summary>Deletes a directory and everything under it without following a link out of the tree.</summary>
    /// <param name="directoryPath">The root of the tree.</param>
    /// <remarks>
    /// A link inside the tree is removed as a link, so its target survives; the tree the caller asked to delete is
    /// the only tree that goes. A missing path is not an error. A read-only file is made writable first.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The process may not delete part of the tree.</exception>
    /// <exception cref="IOException">Part of the tree could not be deleted, for example because a file is open.</exception>
    void DeleteTreeNoFollow(string directoryPath);

    /// <summary>Creates a symbolic link to an existing file.</summary>
    /// <param name="linkPath">The link to create; its parent directory is created when missing.</param>
    /// <param name="targetPath">The file the link names.</param>
    /// <exception cref="IOException">The link could not be created, or something is already at the link path.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The process may not create the link. On Windows this includes a developer who has not enabled symbolic-link
    /// creation for their account.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">The platform cannot create symbolic links.</exception>
    void CreateFileLink(string linkPath, string targetPath);

    /// <summary>Creates a symbolic link to an existing directory.</summary>
    /// <param name="linkPath">The link to create; its parent directory is created when missing.</param>
    /// <param name="targetPath">The directory the link names.</param>
    /// <exception cref="IOException">The link could not be created, or something is already at the link path.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The process may not create the link. On Windows this includes a developer who has not enabled symbolic-link
    /// creation for their account.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">The platform cannot create symbolic links.</exception>
    void CreateDirectoryLink(string linkPath, string targetPath);

    /// <summary>Creates a new, empty directory with a unique name under the system temporary directory.</summary>
    /// <param name="prefix">A recognizable start for the directory's name, such as a tool name.</param>
    /// <returns>The absolute path of the directory this call created.</returns>
    /// <remarks>The name is random, so two callers never receive the same directory. The prefix is a name fragment,
    /// not a path: it may not be rooted and may not contain a directory separator.</remarks>
    /// <exception cref="ArgumentException">The prefix is empty, rooted, or contains a separator or <c>..</c>.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not create the directory.</exception>
    /// <exception cref="IOException">The directory could not be created.</exception>
    string CreateTemporaryDirectory(string prefix);
}
