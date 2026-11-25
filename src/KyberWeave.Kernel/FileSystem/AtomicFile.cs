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
/// <para>
/// By default a target that is a symbolic link is replaced by a regular file, and the file the link named
/// is left untouched. A caller writing a file people commonly keep as a link, such as VS Code's
/// <c>settings.json</c> in a dotfiles repository, opts in to writing through instead: the link's final
/// target is replaced and the link itself stays. Writing through is opt-in because it writes to wherever
/// the link points, so it belongs only where the operator owns the link. The temporary is created beside
/// the final target, so the rename stays within one volume. A dangling link creates its target.
/// </para>
/// <para>
/// By default the replaced file takes the mode a new file gets, because the temporary is a new file. A caller
/// writing a file that holds secrets, such as a harness configuration kept at <c>0600</c>, opts in to keeping the
/// existing file's Unix mode instead; otherwise every write would silently widen it to the umask default. The mode
/// is read from the file a link names, never from the link, and set on the temporary before the rename, so the
/// destination never holds the new content under a wider mode. It is opt-in so the callers that predate it keep
/// their behaviour. Nothing is copied on Windows, which has no Unix mode, or when there is no existing file.
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
    /// <param name="writeThroughSymlink">True to replace the file a symbolic link at <paramref name="path"/> points to, keeping the link.</param>
    /// <exception cref="InvalidOperationException">The path has no parent directory.</exception>
    public static void WriteAllBytes(string path, byte[] content, bool writeThroughSymlink = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, writeThroughSymlink, temporaryPath => File.WriteAllBytes(temporaryPath, content));
    }

    /// <summary>Writes bytes atomically, optionally forcing the temporary's bytes to the device before the rename.</summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="content">The complete bytes the file ends up holding.</param>
    /// <param name="writeThroughSymlink">True to replace the file a symbolic link at <paramref name="path"/> points to, keeping the link.</param>
    /// <param name="flushToDisk">True to write through the operating system's cache and flush the file to the device, so a crash after the rename cannot lose the content.</param>
    public static void WriteAllBytes(string path, byte[] content, bool writeThroughSymlink, bool flushToDisk)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!flushToDisk)
        {
            WriteAllBytes(path, content, writeThroughSymlink);
            return;
        }

        Write(path, writeThroughSymlink, temporaryPath => WriteFlushed(temporaryPath, content));
    }

    /// <summary>Writes bytes atomically, optionally keeping the replaced file's Unix mode.</summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="content">The complete bytes the file ends up holding.</param>
    /// <param name="writeThroughSymlink">True to replace the file a symbolic link at <paramref name="path"/> points to, keeping the link.</param>
    /// <param name="flushToDisk">True to write through the operating system's cache and flush the file to the device, so a crash after the rename cannot lose the content.</param>
    /// <param name="preserveUnixFileMode">True to give the new file the Unix mode of the file it replaces, read from the file a link names; false gives it the default mode of a new file.</param>
    /// <exception cref="InvalidOperationException">The path has no parent directory.</exception>
    public static void WriteAllBytes(string path, byte[] content, bool writeThroughSymlink, bool flushToDisk, bool preserveUnixFileMode)
    {
        ArgumentNullException.ThrowIfNull(content);
        Action<string> writeTemporary = flushToDisk
            ? temporaryPath => WriteFlushed(temporaryPath, content)
            : temporaryPath => File.WriteAllBytes(temporaryPath, content);
        Write(path, writeThroughSymlink, writeTemporary, preserveUnixFileMode);
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
        Write(path, writeThroughSymlink: false, temporaryPath => File.WriteAllText(temporaryPath, content, PlainUtf8));
    }

    /// <summary>Writes text atomically, optionally forcing the temporary's bytes to the device before the rename.</summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="content">The text, written as UTF-8 without a byte-order mark.</param>
    /// <param name="flushToDisk">True to write through the operating system's cache and flush the file to the device, so a crash after the rename cannot lose the content.</param>
    public static void WriteAllText(string path, string content, bool flushToDisk)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!flushToDisk)
        {
            WriteAllText(path, content);
            return;
        }

        Write(
            path,
            writeThroughSymlink: false,
            temporaryPath => WriteFlushed(temporaryPath, PlainUtf8.GetBytes(content)));
    }

    private static void WriteFlushed(string temporaryPath, byte[] content)
    {
        using FileStream stream = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static void Write(string path, bool writeThroughSymlink, Action<string> writeTemporary, bool preserveUnixFileMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeTemporary);

        // Resolved before the directory is split off, because a caller may hand over a bare file
        // name. GetDirectoryName("candidates.json") is the empty string rather than a directory,
        // and CreateDirectory("") throws instead of writing beside the working directory the caller
        // meant.
        string destination = Path.GetFullPath(path);
        if (writeThroughSymlink)
            destination = ResolveLinks(destination);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"The path '{path}' has no parent directory.");

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            writeTemporary(temporaryPath);
            if (preserveUnixFileMode)
                CopyUnixFileMode(destination, temporaryPath);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            Discard(temporaryPath);
        }
    }

    /// <summary>
    /// Gives the temporary the Unix mode of the file it is about to replace, so the rename cannot widen who may
    /// read it.
    /// </summary>
    /// <remarks>
    /// The destination is resolved through any link here rather than trusting the caller to have done it, so the
    /// mode is always the named file's: a link's own mode is meaningless on Linux and not what a reader is checked
    /// against. A dangling link resolves to a missing file and, like a missing destination, leaves the default mode.
    /// </remarks>
    /// <param name="destination">The fully qualified path the temporary is about to be renamed over.</param>
    /// <param name="temporaryPath">The fully written temporary sibling.</param>
    private static void CopyUnixFileMode(string destination, string temporaryPath)
    {
        if (OperatingSystem.IsWindows())
            return;

        string resolved = ResolveLinks(destination);
        if (!File.Exists(resolved))
            return;

        File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(resolved));
    }

    /// <summary>
    /// Follows a symbolic link at <paramref name="destination"/> to its final target, so the write replaces
    /// the file the link names instead of the link. A path that is not a link is returned unchanged.
    /// </summary>
    /// <param name="destination">The fully qualified path the caller asked to write.</param>
    private static string ResolveLinks(string destination)
    {
        // LinkTarget is null for a regular file and for a path that does not exist; ResolveLinkTarget throws on the latter.
        FileInfo file = new(destination);
        if (file.LinkTarget is null)
            return destination;

        FileSystemInfo? target = file.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? destination : Path.GetFullPath(target.FullName);
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
