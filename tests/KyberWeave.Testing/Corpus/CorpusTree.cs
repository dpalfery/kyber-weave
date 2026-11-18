using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>Reads the directory tree of a corpus, which <see cref="ICorpusSource"/> exposes one level at a time.</summary>
/// <remarks>
/// The seam lists one directory's files and one directory's children; a scan that has to see every entry
/// under a directory, files and directories alike, builds that view here rather than walking a real tree.
/// </remarks>
internal static class CorpusTree
{
    /// <summary>Lists every entry under a corpus directory: its files and its directories, recursively.</summary>
    /// <param name="corpus">The corpus to read.</param>
    /// <param name="relativeDirectory">The directory's path relative to the corpus root, with forward slashes.</param>
    /// <returns>Corpus-relative paths, files and directories, with forward slashes.</returns>
    public static IEnumerable<string> Entries(ICorpusSource corpus, string relativeDirectory) =>
        corpus.EnumerateFiles(relativeDirectory).Concat(Directories(corpus, relativeDirectory));

    /// <summary>Gets whether a corpus directory exists, comparing the path's case as the corpus's file system does.</summary>
    /// <param name="corpus">The corpus to read.</param>
    /// <param name="relativePath">The directory's path relative to the corpus root, with forward slashes.</param>
    /// <returns>True when the directory exists.</returns>
    public static bool DirectoryExists(ICorpusSource corpus, string relativePath)
    {
        string path = relativePath.Replace('\\', '/').TrimEnd('/');
        int slash = path.LastIndexOf('/');
        string parent = slash < 0 ? string.Empty : path[..slash];
        StringComparison comparison = NameComparison(corpus);

        return corpus.EnumerateDirectories(parent)
            .Any(child => string.Equals(child, path, comparison));
    }

    /// <summary>Gets the name comparison the corpus's file system uses, which its seam contract declares.</summary>
    /// <param name="corpus">The corpus being read.</param>
    /// <returns>Ordinal on Linux; case-insensitive on the case-preserving APFS and NTFS rules.</returns>
    private static StringComparison NameComparison(ICorpusSource corpus) =>
        corpus is InMemoryCorpusSource memory
            ? memory.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
            : OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Lists every directory under a corpus directory, recursively, excluding the directory itself.</summary>
    /// <param name="corpus">The corpus to read.</param>
    /// <param name="relativeDirectory">The directory's path relative to the corpus root, with forward slashes.</param>
    /// <returns>Corpus-relative directory paths, with forward slashes.</returns>
    public static IEnumerable<string> Directories(ICorpusSource corpus, string relativeDirectory) =>
        corpus
            .EnumerateDirectories(relativeDirectory)
            .SelectMany(child => Directories(corpus, child).Prepend(child));
}
