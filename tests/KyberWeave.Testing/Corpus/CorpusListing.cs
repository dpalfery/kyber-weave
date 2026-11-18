namespace KyberWeave.Tests.Corpus;

/// <summary>The listing rules both corpus implementations share, so a listing means the same thing on a disk and in memory.</summary>
internal static class CorpusListing
{
    /// <summary>The directory names a recursive file listing does not descend into.</summary>
    public static readonly string[] SkippedDirectoryNames =
    [
        "bin", "obj", "node_modules", ".git", ".venv", "dist", "out", "coverage", "TestResults", "artifacts", "target"
    ];

    /// <summary>Gets whether a canonical path sits in a directory a recursive listing skips.</summary>
    /// <param name="directory">The directory the listing started at, as a canonical path.</param>
    /// <param name="path">The file's canonical path.</param>
    /// <param name="comparison">The corpus's name comparison, which follows its host file system.</param>
    /// <returns>True when a directory between the listing's directory and the file is a skipped one.</returns>
    public static bool HasSkippedDirectory(string directory, string path, StringComparison comparison)
    {
        string prefix = directory.Length == 0 ? string.Empty : directory + "/";
        if (!path.StartsWith(prefix, comparison))
            return false;

        string[] segments = path[prefix.Length..].Split('/');
        StringComparer names = comparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        return segments
            .Take(segments.Length - 1)
            .Any(segment => SkippedDirectoryNames.Contains(segment, names));
    }
}
