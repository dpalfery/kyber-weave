using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Update;

namespace KyberWeave.Data.Update;

/// <summary>Puts an extracted binary in place of an installed one, so the installed path always holds a whole image.</summary>
/// <remarks>
/// The new image is staged beside the destination and moved over it in one rename. On Windows the running image
/// cannot be overwritten, so the old one is moved aside first and put back if the move of the new one fails.
/// </remarks>
internal sealed class BinaryReplacer(
    IWritableFileSystem files,
    IUpdateFileModes modes,
    IProcessRunner processes)
{
    /// <summary>The bound for clearing quarantine on one staged file. xattr returns at once, so a longer wait means it hung.</summary>
    private static readonly TimeSpan XattrTimeout = TimeSpan.FromSeconds(30);

    private const UnixFileMode ExecutableBits =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>Replaces an installed binary.</summary>
    /// <param name="extractedBinaryPath">The new binary.</param>
    /// <param name="destinationPath">The installed binary to replace.</param>
    /// <param name="windows">Whether the platform is Windows.</param>
    /// <param name="macOs">Whether the platform is macOS, which clears the quarantine attribute first.</param>
    /// <param name="cancellationToken">Cancels the quarantine-attribute child.</param>
    /// <exception cref="SelfUpdateException">The archive did not hold the binary, or the destination has no directory.</exception>
    public async Task ReplaceAsync(
        string extractedBinaryPath,
        string destinationPath,
        bool windows,
        bool macOs,
        CancellationToken cancellationToken)
    {
        if (!files.FileExists(extractedBinaryPath))
        {
            throw new SelfUpdateException(
                $"archive did not contain {Path.GetFileName(extractedBinaryPath)}");
        }

        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new SelfUpdateException($"cannot install to '{destinationPath}'.");
        files.CreateDirectory(directory);

        string staging = Path.Combine(directory, "." + Path.GetFileName(destinationPath) + ".new");
        try
        {
            CopyContent(extractedBinaryPath, staging);
            if (!windows)
                modes.Set(staging, ExecutableBits);

            // Quarantine is cleared on the staging copy, before the move, not on the installed
            // path afterwards. The move carries extended attributes with the inode, so the
            // result is the same — but staging is the last point at which the destination is
            // certainly not this process's own image. See ClearMacQuarantine.
            if (macOs)
                await ClearMacQuarantineAsync(staging, cancellationToken).ConfigureAwait(false);

            if (windows)
                MoveOverOnWindows(staging, destinationPath);
            else
                files.MoveFile(staging, destinationPath, overwrite: true);
        }
        catch
        {
            RemoveQuietly(staging);
            throw;
        }
    }

    private void MoveOverOnWindows(string staging, string destinationPath)
    {
        string oldPath = destinationPath + ".old";
        if (!files.FileExists(destinationPath))
        {
            // A stop between the two moves below left no image and the old one here. The new image takes its place and
            // the old one stays: if this commit fails and the update rolls back, it is the only image the user has. The
            // next normal update removes it.
            files.MoveFile(staging, destinationPath, overwrite: true);
            return;
        }

        files.DeleteFile(oldPath);
        files.MoveFile(destinationPath, oldPath, overwrite: true);
        try
        {
            files.MoveFile(staging, destinationPath, overwrite: true);
        }
        catch
        {
            if (files.FileExists(oldPath) && !files.FileExists(destinationPath))
                files.MoveFile(oldPath, destinationPath, overwrite: true);
            throw;
        }

        RemoveQuietly(oldPath);
    }

    private void RemoveQuietly(string path)
    {
        try
        {
            files.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The previous image stays locked until this process exits, and a staging file that cannot be removed is
            // overwritten by the next update.
        }
    }

    /// <summary>Copies a file whole: written beside the destination, then moved over it, so a stop leaves the old or the new content.</summary>
    /// <remarks>
    /// The copy is staged under the name <see cref="ReplaceAsync"/> stages under, so a copy that a stop left behind is the file
    /// the next update overwrites and moves, not a stray.
    /// </remarks>
    /// <param name="sourcePath">The file to copy.</param>
    /// <param name="destinationPath">Where the copy goes.</param>
    /// <param name="overwrite">True to replace an existing destination.</param>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="IOException">The destination exists and <paramref name="overwrite"/> is false.</exception>
    public void Copy(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!files.FileExists(sourcePath))
            throw new FileNotFoundException($"Could not find file '{sourcePath}'.", sourcePath);

        if (!overwrite && files.PathExists(destinationPath))
            throw new IOException($"The file '{destinationPath}' already exists.");

        string directory = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        string staging = Path.Combine(directory, "." + Path.GetFileName(destinationPath) + ".new");
        try
        {
            CopyContent(sourcePath, staging);
            UnixFileMode? mode = modes.Get(sourcePath);
            if (mode is { } bits)
                modes.Set(staging, bits);

            files.MoveFile(staging, destinationPath, overwrite);
        }
        catch
        {
            RemoveQuietly(staging);
            throw;
        }
    }

    private void CopyContent(string sourcePath, string stagingPath)
    {
        files.DeleteFile(stagingPath);
        using Stream source = files.OpenRead(sourcePath);
        using Stream staging = files.OpenWriteNew(stagingPath);
        source.CopyTo(staging);
    }

    /// <summary>Strips <c>com.apple.quarantine</c> from a staged binary.</summary>
    /// <remarks>
    /// Call this before the staged file is moved over its destination, never after. This is
    /// not the first code in an update to touch <c>System.Diagnostics.Process</c>, because the
    /// hand-off rehearsal starts a process before the first commit. A single-file host resolves
    /// a bundled assembly by re-opening its own executable by path. Once the destination is the running <c>kyber-weave</c>, that path holds the
    /// newly installed image and the load fails with "Could not load file or assembly
    /// 'System.Diagnostics.Process'" — aborting an update that had already written the new
    /// binary. Running before the move also warms the assembly for anything after it.
    /// </remarks>
    private async Task ClearMacQuarantineAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            ProcessStartInfo startInfo = new("xattr")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add("com.apple.quarantine");
            startInfo.ArgumentList.Add(path);

            await processes.RunAsync(startInfo, string.Empty, XattrTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                       or UnauthorizedAccessException or TimeoutException)
        {
            // Best-effort: xattr is not required for the binaries to run. A hung xattr is stopped by the
            // runner at XattrTimeout and lands here like any other failure to clear the attribute.
        }
    }
}
