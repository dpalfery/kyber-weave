using System.Text;

namespace KyberWeave.Core.FileSystem;

/// <summary>
/// Replaces or creates a file by renaming a fully written temporary sibling over it, so the target
/// is never observable half-written.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="File.WriteAllBytes(string, byte[])"/> truncates in place: a process killed between
/// the truncation and the last byte leaves a zero-length or partial document where a complete one
/// used to be. The files this engine writes are inherited rather than private — a harness
/// configuration a whole team commits, a receipt that records what Kyber owns, the glossary the
/// analysis merges into, the review bundle an operator hands to a reviewing agent — and a
/// truncated copy of one is worse than no copy, because the code that reads it cannot tell a short
/// file from a complete one.
/// </para>
/// <para>
/// The temporary is a unique sibling in the target's own directory, never a predictable
/// <c>&lt;path&gt;.tmp</c> and never a file in the system temporary directory. Rename is atomic
/// only within a volume, so a temporary elsewhere can silently degrade into a copy. A predictable
/// name collides instead: two runs writing the same receipt would share one temporary, and each
/// could publish a blend of the other's bytes.
/// </para>
/// <para>
/// The temporary is removed in a <c>finally</c>, so a write that fails — a locked target, a full
/// disk, a directory the process may not write — leaves the directory as it found it. A leftover
/// temporary is litter in a directory the operator never asked this tool to touch, and
/// <c>git add .</c> commits it.
/// </para>
/// <para>
/// That removal is best-effort, not a second failure the caller has to survive. A disk full enough
/// to fail the write usually cannot delete either, and an unguarded delete in the <c>finally</c>
/// would replace the exception the caller is told about with the cleanup's own: the write failed
/// and the sweep failed, and the message reports only the sweep. The caller needs to know why the
/// document was never written; the temporary left behind is the smaller harm.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>
    /// UTF-8 without a byte-order mark, which is what <see cref="File.WriteAllText(string, string)"/>
    /// writes and what every text file this engine produces is expected to hold.
    /// </summary>
    /// <remarks>
    /// The one input the two disagree on is an unpaired surrogate: the framework overload throws on
    /// it and this instance writes U+FFFD. The difference is unreachable from the callers this
    /// serves — each supplies text it read as a document or serialised as JSON, and the serialiser
    /// substitutes U+FFFD before the encoder sees the string.
    /// </remarks>
    private static readonly UTF8Encoding PlainUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> atomically.</summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="content">The complete bytes the file ends up holding.</param>
    /// <exception cref="InvalidOperationException">The path has no parent directory.</exception>
    public static void WriteAllBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, temporaryPath => File.WriteAllBytes(temporaryPath, content));
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically as UTF-8 without a
    /// byte-order mark.
    /// </summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="content">The complete text the file ends up holding.</param>
    /// <exception cref="InvalidOperationException">The path has no parent directory.</exception>
    public static void WriteAllText(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, temporaryPath => File.WriteAllText(temporaryPath, content, PlainUtf8));
    }

    private static void Write(string path, Action<string> writeTemporary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeTemporary);

        // Resolved before the directory is split off, because a caller may hand over a bare file
        // name. GetDirectoryName("candidates.json") is the empty string rather than a directory,
        // and CreateDirectory("") throws instead of writing beside the working directory the caller
        // meant.
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"The path '{path}' has no parent directory.");

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            writeTemporary(temporaryPath);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            Discard(temporaryPath);
        }
    }

    /// <summary>
    /// Deletes the temporary, swallowing a cleanup failure so it cannot mask the failure that left
    /// the temporary behind.
    /// </summary>
    /// <param name="temporaryPath">The sibling temporary, present only if the write or move failed.</param>
    private static void Discard(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort. The write or the move already threw, and that exception is the one the
            // caller is told to diagnose; a delete failure here would otherwise take its place.
        }
    }
}
