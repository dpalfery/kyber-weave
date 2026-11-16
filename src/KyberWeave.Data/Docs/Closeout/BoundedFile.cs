using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>
/// Reads a text file this tool, or a person, put in the repository or its scratch folder, without letting one that
/// has grown absurdly large be loaded whole.
/// </summary>
internal static class BoundedFile
{
    /// <summary>The most bytes read from one such file: far above any real task file, gate report or template.</summary>
    internal const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>
    /// The file's text, as <see cref="File.ReadAllText(string)"/> reads it, through the one bounded reader. A file larger
    /// than <see cref="MaxBytes"/> is not read: it throws <see cref="InvalidDataException"/> naming the size, so a caller
    /// that already reports an unreadable file reports this one the same way.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is larger than <see cref="MaxBytes"/>.</exception>
    internal static string ReadAllText(string path) => BoundedFileReader.ReadAllText(path, MaxBytes);
}
