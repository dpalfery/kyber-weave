namespace KyberWeave.Cli.Update;

/// <summary>The names a Release archive and its installed binary carry.</summary>
public static class BinaryInstaller
{
    /// <summary>The archive file name for a binary on a platform: <c>.zip</c> on Windows, <c>.tar.gz</c> elsewhere.</summary>
    /// <param name="binaryBaseName">The binary's base name, such as <c>kyber-weave</c>.</param>
    /// <param name="rid">The platform runtime identifier.</param>
    /// <returns>The archive file name.</returns>
    public static string ArchiveName(string binaryBaseName, string rid)
    {
        string extension = PlatformRid.IsWindowsRid(rid) ? ".zip" : ".tar.gz";
        return $"{binaryBaseName}-{rid}{extension}";
    }

    /// <summary>The installed file name for a binary: with <c>.exe</c> on Windows.</summary>
    /// <param name="binaryBaseName">The binary's base name.</param>
    /// <param name="windows">Whether the platform is Windows.</param>
    /// <returns>The installed file name.</returns>
    public static string InstalledFileName(string binaryBaseName, bool windows) =>
        windows ? binaryBaseName + ".exe" : binaryBaseName;
}
