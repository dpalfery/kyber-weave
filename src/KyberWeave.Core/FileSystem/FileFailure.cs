using System.Security;

namespace KyberWeave.Core.FileSystem;

/// <summary>
/// The one answer to "did this file read, stat or enumeration fail because of the file system": an I/O error, a
/// permission refusal (including the <see cref="SecurityException"/> a locked-down root raises), or a name the file
/// system cannot take (a malformed, over-long or unsupported path). Every such failure is an answer ("could not be
/// read"), not a crash; every catch around file access uses this rather than its own list.
/// </summary>
public static class FileFailure
{
    /// <summary>Whether <paramref name="exception"/> is a way touching a file or directory can fail.</summary>
    /// <param name="exception">The exception a file-access call threw.</param>
    /// <returns><see langword="true"/> for a file-system failure; <see langword="false"/> for anything else.</returns>
    public static bool Is(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
