using System.Globalization;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Writes small state files atomically with owner-only permissions, refusing to follow a symbolic
/// link at either the directory or the target so a compromised state directory cannot redirect them.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Creates <paramref name="stateDir"/> when absent and refuses a symbolic link at that path.
    /// </summary>
    /// <param name="stateDir">Directory that will hold the file.</param>
    internal static string EnsureSafeDirectory(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        if (Directory.Exists(fullDir))
        {
            RefuseSymlink(fullDir);
            return fullDir;
        }

        DirectoryInfo created = Directory.CreateDirectory(fullDir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                created.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return fullDir;
    }

    /// <summary>
    /// Refuses to operate on <paramref name="path"/> when it exists as a symbolic link.
    /// </summary>
    /// <param name="path">File or directory path to inspect.</param>
    internal static void RefuseSymlink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to traverse symbolic link at '{path}'");
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> through a sibling temporary file,
    /// setting <c>0600</c> on Unix before the rename so the content is never briefly world-readable.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="bytes">Content to write.</param>
    /// <param name="pid">Process id embedded in the temporary file name.</param>
    internal static void Write(string path, ReadOnlySpan<byte> bytes, int? pid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));
        EnsureSafeDirectory(directory);
        RefuseSymlink(fullPath);

        string tempPath = Path.Combine(
            directory,
            string.Create(
                CultureInfo.InvariantCulture,
                $".{Path.GetFileName(fullPath)}.{pid ?? Environment.ProcessId}.{Guid.NewGuid():N}.tmp"));

        using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(bytes);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(tempPath, fullPath, overwrite: true);
    }
}
