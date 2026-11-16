using System.IO.Compression;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Skills;

/// <summary>Writes a skill bundle as a .zip file on the real file system.</summary>
public sealed class ZipSkillArchiveWriter : ISkillArchiveWriter
{
    /// <inheritdoc />
    public void Write(string output, IReadOnlyList<SkillArchiveEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(entries);

        using ZipArchive zip = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (SkillArchiveEntry entry in entries)
        {
            _ = zip.CreateEntryFromFile(entry.SourcePath, entry.EntryName);
        }
    }
}
