namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the lines of a file a seat's finding cites, which the seat named and the review does not control.</summary>
public interface ICitedSourceFiles
{
    /// <summary>
    /// The full path of a file inside the repository, as long as it exists, is no larger than <paramref name="maxBytes"/>
    /// and is reached without a symbolic link; null for a name that is outside the repository, absent or a link.
    /// </summary>
    /// <param name="repositoryRoot">The repository root the file is resolved against.</param>
    /// <param name="file">The seat-written file name.</param>
    /// <param name="maxBytes">The most bytes the file may hold.</param>
    /// <exception cref="IOException">The name or the file could not be read; the other file-system and path failures are possible too.</exception>
    string? Resolve(string repositoryRoot, string file, long maxBytes);

    /// <summary>Reads <paramref name="count"/> lines starting at line <paramref name="firstLine"/>, without reading the rest of the file into memory.</summary>
    /// <param name="path">A path <see cref="Resolve"/> returned.</param>
    /// <param name="firstLine">The first line to read, counted from 1.</param>
    /// <param name="count">The most lines to read.</param>
    string[] ReadWindow(string path, long firstLine, long count);
}
