namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads a file whose size the caller does not control, refusing one over a bound.</summary>
public interface IBoundedFileReader
{
    /// <summary>Reads a whole file's bytes.</summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="maxBytes">The most bytes the file may hold.</param>
    /// <returns>The bytes; null when the file is missing, holds more than <paramref name="maxBytes"/>, or cannot be read.</returns>
    byte[]? ReadBytes(string path, int maxBytes);
}
