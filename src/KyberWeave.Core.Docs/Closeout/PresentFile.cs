namespace KyberWeave.Core.Docs.Closeout;

/// <summary>
/// Reads a file that may or may not be there, in one step. A separate "does it exist" check followed by a read leaves a
/// gap in which the file can appear or vanish, so the answer "absent" and the file's text would come from two moments.
/// </summary>
internal static class PresentFile
{
    /// <summary>
    /// The file's text, or null when the file or its folder is not there at the moment of the read. A file that is there
    /// but cannot be read (permissions, a lock, a folder in its place) still throws, so it is never mistaken for absent.
    /// A file larger than <see cref="BoundedFile.MaxBytes"/> is not loaded: it throws <see cref="IOException"/> naming the
    /// size, so every caller that already reports an unreadable file reports this one the same way.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read, or is larger than <see cref="BoundedFile.MaxBytes"/>.</exception>
    internal static string? ReadIfPresent(string path)
    {
        try
        {
            return BoundedFile.ReadAllText(path);
        }
        catch (InvalidDataException exception)
        {
            throw new IOException($"'{Path.GetFileName(path)}' was not read: {exception.Message}", exception);
        }
        catch (FileNotFoundException) when (Directory.Exists(path))
        {
            // The size check reports a folder as a missing file; a folder in the file's place is a failure, not an absence.
            throw new IOException($"'{Path.GetFileName(path)}' is a folder, not a file.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
