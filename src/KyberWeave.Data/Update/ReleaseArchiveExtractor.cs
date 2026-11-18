using System.Formats.Tar;
using System.IO.Compression;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Cli.Update;

/// <summary>Extracts a downloaded release archive into a fresh directory, writing only where the planner allows.</summary>
/// <remarks>
/// The archive is read as a stream the file-system seam opened, and every entry is written back through the seam, so
/// the whole extraction runs on the in-memory file system. A link, a device or any entry that is not a plain file or
/// a directory is refused: a release archive holds binaries only, and a link is how an entry escapes a directory.
/// </remarks>
internal sealed class ReleaseArchiveExtractor(
    IWritableFileSystem files,
    ArchiveEntryPlanner planner,
    IUpdateFileModes modes)
{
    private const UnixFileMode PermissionBits = (UnixFileMode)0x1FF;

    /// <summary>Extracts an archive over a destination directory.</summary>
    /// <param name="archivePath">The downloaded archive.</param>
    /// <param name="destinationDirectory">Where to extract; created when missing.</param>
    /// <param name="windows">Whether the archive is a <c>.zip</c> rather than a <c>.tar.gz</c>.</param>
    /// <exception cref="SelfUpdateException">An entry was refused.</exception>
    public void Extract(string archivePath, string destinationDirectory, bool windows)
    {
        files.CreateDirectory(destinationDirectory);
        using Stream archive = files.OpenRead(archivePath);
        if (windows)
            ExtractZip(archive, destinationDirectory);
        else
            ExtractTarGz(archive, destinationDirectory);
    }

    private void ExtractZip(Stream archive, string destination)
    {
        using ZipArchive zip = new(archive, ZipArchiveMode.Read, leaveOpen: true);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string target = Contained(destination, planner.Resolve(destination, entry.FullName), entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                files.CreateDirectory(target);
                continue;
            }

            using Stream content = entry.Open();
            WriteFile(target, content);
        }
    }

    private void ExtractTarGz(Stream archive, string destination)
    {
        using GZipStream gzip = new(archive, CompressionMode.Decompress, leaveOpen: true);
        using TarReader reader = new(gzip, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
                continue;

            string target = Contained(destination, planner.Resolve(destination, entry.Name), entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    files.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    WriteFile(target, entry.DataStream ?? Stream.Null);
                    modes.Set(target, entry.Mode & PermissionBits);
                    break;
                default:
                    throw new SelfUpdateException(
                        $"the archive entry '{entry.Name}' was refused: a release archive holds plain files only, not a {entry.EntryType}.");
            }
        }
    }

    // The planner already refuses an escaping entry through the path seam. This repeats the containment check where
    // the entry is written, with the framework calls a static analyser recognises, so the guard is visible at the sink.
    private static string Contained(string destination, string target, string entryName)
    {
        string root = Path.GetFullPath(destination);
        string full = Path.GetFullPath(target, root);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(full, root, comparison) && !full.StartsWith(prefix, comparison))
            throw new SelfUpdateException($"the archive entry '{entryName}' was refused: it resolves outside the extraction directory.");

        return full;
    }

    private void WriteFile(string target, Stream content)
    {
        if (Path.GetDirectoryName(target) is { Length: > 0 } parent)
            files.CreateDirectory(parent);

        files.DeleteFile(target);
        using Stream destination = files.OpenWriteNew(target);
        content.CopyTo(destination);
    }
}
