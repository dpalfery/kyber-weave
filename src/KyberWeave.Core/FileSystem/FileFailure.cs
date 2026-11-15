using System.Security;

namespace KyberWeave.Core.FileSystem;

/// <summary>
/// The one answer to "did this file read, stat or enumeration fail because of the file system": an I/O error, a
/// permission refusal (including the <see cref="SecurityException"/> a locked-down root raises), or a name the file
/// system cannot take (a malformed, over-long or unsupported path). Every such failure is an answer ("could not be
/// read"), not a crash. The catches that guard a plain read, stat or enumeration use this; a few deliberately keep their
/// own narrower sets because they react to a specific failure (a lost race on a claim or link file, a retry on a busy
/// file, or a process or stream error that is not file access), so those are not file failures in this sense. An
/// <see cref="ArgumentOutOfRangeException"/> is never one: it is how a caller's bad argument (such as a read limit) is
/// reported, and swallowing it would hide a programming error.
/// </summary>
public static class FileFailure
{
    /// <summary>Whether <paramref name="exception"/> is a way touching a file or directory can fail.</summary>
    /// <param name="exception">The exception a file-access call threw.</param>
    /// <returns><see langword="true"/> for a file-system failure; <see langword="false"/> for anything else.</returns>
    public static bool Is(Exception exception) =>
        exception is not ArgumentOutOfRangeException
        && exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
