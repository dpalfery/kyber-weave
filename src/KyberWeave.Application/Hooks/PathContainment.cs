namespace KyberWeave.Domain.Hooks;

/// <summary>Decides whether a path lies inside a directory, comparing whole path segments.</summary>
public static class PathContainment
{
    /// <summary>Checks that a path equals a directory or sits below it, so <c>/repo</c> does not contain <c>/repo-other</c>. Both sides are normalized first, so <c>..</c> segments cannot escape.</summary>
    /// <param name="directory">The containing directory, absolute.</param>
    /// <param name="path">The absolute path to test.</param>
    /// <returns>True when <paramref name="path"/> is <paramref name="directory"/> or below it; false when either input is empty or relative.</returns>
    /// <remarks>Comparison ignores case on Windows and macOS and is case-sensitive elsewhere, matching the usual file system behaviour.</remarks>
    public static bool IsWithin(string directory, string path) =>
        IsWithinUsing(directory, path, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool IsWithinUsing(string directory, string path, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(path);

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(path);
        return target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(root, comparison)
            || target.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || target.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }
}
