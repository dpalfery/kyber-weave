namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads a file a seat or an index names, only when it lies inside the repository and is reached without a symbolic link.</summary>
public interface IContainedFileReader
{
    /// <summary>Whether a path is a file inside the repository, reached without a symbolic link that leaves it.</summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <param name="relativePath">The file's repository-relative path.</param>
    bool IsContainedFile(string repositoryRoot, string relativePath);

    /// <summary>Reads a file's lines under a size bound.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="maxBytes">The most bytes the file may hold.</param>
    /// <exception cref="InvalidDataException">The file holds more than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="IOException">The file could not be read; the other file-system failures, such as <see cref="UnauthorizedAccessException"/>, are possible too.</exception>
    string[] ReadLines(string path, long maxBytes);
}
