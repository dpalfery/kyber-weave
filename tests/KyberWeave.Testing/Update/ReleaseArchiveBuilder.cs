using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace KyberWeave.Testing.Update;

/// <summary>One entry of a test archive.</summary>
/// <param name="Name">The entry name exactly as the archive spells it, so a hostile name can be built.</param>
/// <param name="Content">The file's text; ignored for a directory or a link.</param>
/// <param name="Type">The tar entry type; a regular file by default.</param>
/// <param name="LinkTarget">The target of a link entry.</param>
public sealed record ReleaseArchiveEntry(
    string Name,
    string Content = "",
    TarEntryType Type = TarEntryType.RegularFile,
    string? LinkTarget = null);

/// <summary>Builds the release archives an update downloads: a <c>.tar.gz</c> for Unix and a <c>.zip</c> for Windows, held in memory.</summary>
public static class ReleaseArchiveBuilder
{
    /// <summary>Builds a gzip-compressed tar archive.</summary>
    /// <param name="entries">The entries, in order.</param>
    /// <returns>The archive bytes.</returns>
    public static byte[] TarGz(params ReleaseArchiveEntry[] entries)
    {
        using MemoryStream bytes = new();
        using (GZipStream gzip = new(bytes, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (ReleaseArchiveEntry entry in entries)
            {
                PaxTarEntry tar = new(entry.Type, entry.Name);
                tar.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
                if (entry.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                    tar.LinkName = entry.LinkTarget ?? string.Empty;
                else if (entry.Type == TarEntryType.RegularFile)
                    tar.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(entry.Content));

                writer.WriteEntry(tar);
            }
        }

        return bytes.ToArray();
    }

    /// <summary>Builds a zip archive of plain files; a name ending in a separator is a directory.</summary>
    /// <param name="entries">The entries, in order.</param>
    /// <returns>The archive bytes.</returns>
    public static byte[] Zip(params ReleaseArchiveEntry[] entries)
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ReleaseArchiveEntry entry in entries)
            {
                ZipArchiveEntry created = zip.CreateEntry(entry.Name);
                using Stream stream = created.Open();
                stream.Write(Encoding.UTF8.GetBytes(entry.Content));
            }
        }

        return bytes.ToArray();
    }
}
