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
    /// </summary>
    internal static string? ReadIfPresent(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
