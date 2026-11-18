namespace KyberWeave.Data.Paths;

/// <summary>Keeps a trailing separator that the path seam trims, where a caller's behaviour depends on it.</summary>
internal static class TrailingSeparator
{
    /// <summary>Restores the trailing separator <c>Path.GetFullPath</c> kept when the caller spelled the path with one.</summary>
    /// <param name="spelled">The path as the caller wrote it.</param>
    /// <param name="fullPath">The full path the path seam returned.</param>
    /// <returns>The full path, with a separator appended when <paramref name="spelled"/> ended in one.</returns>
    internal static string Keep(string spelled, string fullPath) =>
        Ends(spelled) && !Ends(fullPath) ? fullPath + Path.DirectorySeparatorChar : fullPath;

    private static bool Ends(string path) =>
        path.Length > 0 && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar);
}
