namespace KyberWeave.Domain.Hooks;

/// <summary>Decides whether a path lies inside a directory, comparing whole path segments.</summary>
public static class PathContainment
{
    /// <summary>Checks that a path equals a directory or sits below it, so <c>/repo</c> does not contain <c>/repo-other</c>.</summary>
    /// <param name="directory">The containing directory, absolute.</param>
    /// <param name="path">The absolute path to test.</param>
    /// <returns>True when <paramref name="path"/> is <paramref name="directory"/> or below it.</returns>
    public static bool IsWithin(string directory, string path)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(path);

        string root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(root, StringComparison.Ordinal)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
