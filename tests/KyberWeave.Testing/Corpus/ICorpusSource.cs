namespace KyberWeave.Tests.Corpus;

/// <summary>
/// Reads the shipped content a content test asserts on: the agents, skills, standards, documents and workflows of one
/// repository checkout.
/// </summary>
/// <remarks>
/// <para>
/// Every path, given and returned, is relative to <see cref="Root"/> and written with forward slashes, so a test
/// reads the same way on every platform. A path that is rooted, or that climbs above the root with <c>..</c>, is
/// refused: a corpus read never leaves the corpus.
/// </para>
/// <para>
/// This is test infrastructure, not a product seam. It exists so a content test says what it reads instead of
/// walking the tree to find the repository root, and so a fixture corpus can stand in for the checkout when the
/// shipping content is not the subject. <see cref="CheckoutCorpusSource"/> reads this checkout;
/// <see cref="InMemoryCorpusSource"/> holds a corpus in memory.
/// </para>
/// </remarks>
public interface ICorpusSource
{
    /// <summary>Gets the absolute path of the repository root every relative path in this corpus resolves against.</summary>
    string Root { get; }

    /// <summary>Gets whether the corpus holds a file at a relative path.</summary>
    /// <param name="relativePath">The file's path relative to <see cref="Root"/>, with forward slashes.</param>
    /// <returns>True when the file exists.</returns>
    /// <exception cref="ArgumentException">The path is rooted or climbs above the root.</exception>
    bool Exists(string relativePath);

    /// <summary>Reads a file as text.</summary>
    /// <param name="relativePath">The file's path relative to <see cref="Root"/>, with forward slashes.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="ArgumentException">The path is rooted or climbs above the root.</exception>
    /// <exception cref="FileNotFoundException">The corpus holds no file at the path.</exception>
    string ReadText(string relativePath);

    /// <summary>Reads a file as bytes.</summary>
    /// <param name="relativePath">The file's path relative to <see cref="Root"/>, with forward slashes.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="ArgumentException">The path is rooted or climbs above the root.</exception>
    /// <exception cref="FileNotFoundException">The corpus holds no file at the path.</exception>
    byte[] ReadBytes(string relativePath);

    /// <summary>Lists the files under a directory, as paths relative to <see cref="Root"/> with forward slashes.</summary>
    /// <param name="relativeDirectory">The directory's path relative to <see cref="Root"/>, with forward slashes.</param>
    /// <param name="searchPattern">A file name pattern such as <c>*.md</c>; <c>*</c> when null.</param>
    /// <param name="recursive">True to descend into subdirectories; false to list the directory's own files.</param>
    /// <returns>
    /// The matching files in the platform's order, each relative to <see cref="Root"/> with forward slashes; empty
    /// when the directory is missing.
    /// </returns>
    /// <exception cref="ArgumentException">The path is rooted or climbs above the root.</exception>
    IEnumerable<string> EnumerateFiles(
        string relativeDirectory,
        string searchPattern = "*",
        bool recursive = true);

    /// <summary>Lists the child directories of a directory, as paths relative to <see cref="Root"/>.</summary>
    /// <param name="relativeDirectory">The directory's path relative to <see cref="Root"/>, with forward slashes.</param>
    /// <returns>
    /// One path per immediate child directory, relative to <see cref="Root"/> with forward slashes; empty when the
    /// directory is missing.
    /// </returns>
    /// <exception cref="ArgumentException">The path is rooted or climbs above the root.</exception>
    IEnumerable<string> EnumerateDirectories(string relativeDirectory);
}
