namespace KyberWeave.Tests.FileSystem;

/// <summary>Convenience reads and writes over <see cref="InMemoryFileSystem"/> for tests that mutate a copy of a source tree.</summary>
public static class InMemoryFileSystemExtensions
{
    /// <summary>Appends text to a file, as <see cref="File.AppendAllText(string, string?)"/> does on disk.</summary>
    /// <param name="files">The file system.</param>
    /// <param name="path">The file to append to.</param>
    /// <param name="text">The text to append.</param>
    public static void AppendAllText(this InMemoryFileSystem files, string path, string text)
    {
        ArgumentNullException.ThrowIfNull(files);
        files.WriteAllText(path, files.ReadAllText(path) + text);
    }

    /// <summary>Reads every line of a file into an array.</summary>
    /// <param name="files">The file system.</param>
    /// <param name="path">The file to read.</param>
    /// <returns>The lines.</returns>
    public static string[] ReadAllLines(this InMemoryFileSystem files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        return [.. files.ReadLines(path)];
    }
}
