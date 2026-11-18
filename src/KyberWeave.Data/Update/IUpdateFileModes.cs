namespace KyberWeave.Cli.Update;

/// <summary>Reads and sets the Unix permission bits of a file an update installs.</summary>
/// <remarks>
/// The file-system seam models names and content, not permissions, so the executable bit of an installed binary
/// goes through here. A platform without Unix permissions reports no mode and ignores a set.
/// </remarks>
internal interface IUpdateFileModes
{
    /// <summary>Gets a file's permission bits.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The bits, or null on a platform that has none.</returns>
    UnixFileMode? Get(string path);

    /// <summary>Sets a file's permission bits; does nothing on a platform that has none.</summary>
    /// <param name="path">The file.</param>
    /// <param name="mode">The bits.</param>
    void Set(string path, UnixFileMode mode);
}
