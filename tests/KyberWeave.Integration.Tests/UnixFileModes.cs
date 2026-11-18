namespace KyberWeave.Tests;

/// <summary>
/// Arranges a file this process may not read, for the cases that prove a permission failure is reported
/// rather than swallowed. A Unix file mode expresses the denial; Windows, and a privileged process, cannot
/// arrange it, so the helper answers <see langword="false"/> and the caller skips the case.
/// </summary>
internal static class UnixFileModes
{
    /// <summary>
    /// Denies this process every permission on <paramref name="path"/>, answering false when the platform or
    /// the process cannot carry that denial. The caller restores the mode with <see cref="Restore"/>.
    /// </summary>
    internal static bool TryRemoveReadPermission(string path, out UnixFileMode originalMode)
    {
        originalMode = default;
        if (OperatingSystem.IsWindows())
            return false;

        originalMode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return !CanRead(path);
    }

    /// <summary>Puts a file's original mode back, so the temporary tree can be disposed.</summary>
    internal static void Restore(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, mode);
    }

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllText(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
