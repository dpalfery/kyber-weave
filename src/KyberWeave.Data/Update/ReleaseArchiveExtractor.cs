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
        (string root, string prefix, StringComparison comparison) = Containment(destination);
        using ZipArchive zip = new(archive, ZipArchiveMode.Read, leaveOpen: true);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            // Spelling first (no rooted or '..' name), then lexical containment with Path.GetFullPath and a prefix check
            // in this loop (the form code scanners recognise), then the planner's link-aware check on the checked path.
            string name = ArchiveEntryPlanner.ValidateName(entry.FullName);
            string full = Path.GetFullPath(name.Replace('/', Path.DirectorySeparatorChar), root);
            if (!full.StartsWith(prefix, comparison))
            {
                if (string.Equals(full, root, comparison))
                    continue;

                throw Escaped(entry.FullName);
            }

            string target = planner.Resolve(root, Path.GetRelativePath(root, full));
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
        (string root, string prefix, StringComparison comparison) = Containment(destination);
        using GZipStream gzip = new(archive, CompressionMode.Decompress, leaveOpen: true);
        using TarReader reader = new(gzip, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
                continue;

            // Spelling first (no rooted or '..' name), then lexical containment with Path.GetFullPath and a prefix check
            // in this loop (the form code scanners recognise), then the planner's link-aware check on the checked path.
            string name = ArchiveEntryPlanner.ValidateName(entry.Name);
            string full = Path.GetFullPath(name.Replace('/', Path.DirectorySeparatorChar), root);
            if (!full.StartsWith(prefix, comparison))
            {
                if (string.Equals(full, root, comparison))
                    continue;

                throw Escaped(entry.Name);
            }

            string target = planner.Resolve(root, Path.GetRelativePath(root, full));
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

    private static (string Root, string Prefix, StringComparison Comparison) Containment(string destination)
    {
        // The destination is fully qualified (the planner requires it), so this GetFullPath reads no working directory.
        string root = Path.GetFullPath(destination);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return (root, prefix, comparison);
    }

    private static SelfUpdateException Escaped(string entryName) =>
        new($"the archive entry '{entryName}' was refused: it resolves outside the extraction directory.");

    private void WriteFile(string target, Stream content)
    {
        if (Path.GetDirectoryName(target) is { Length: > 0 } parent)
            files.CreateDirectory(parent);

        files.DeleteFile(target);
        using Stream destination = files.OpenWriteNew(target);
        content.CopyTo(destination);
    }
}
