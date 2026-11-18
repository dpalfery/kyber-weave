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

    /// <summary>Lists every directory under a corpus directory, recursively, excluding the directory itself.</summary>
    /// <param name="corpus">The corpus to read.</param>
    /// <param name="relativeDirectory">The directory's path relative to the corpus root, with forward slashes.</param>
    /// <returns>Corpus-relative directory paths, with forward slashes.</returns>
    public static IEnumerable<string> Directories(ICorpusSource corpus, string relativeDirectory) =>
        corpus
            .EnumerateDirectories(relativeDirectory)
            .SelectMany(child => Directories(corpus, child).Prepend(child));
}
