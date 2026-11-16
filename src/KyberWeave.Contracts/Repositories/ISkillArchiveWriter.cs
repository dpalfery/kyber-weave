namespace KyberWeave.Contracts.Repositories;

/// <summary>One file bundled into a skill archive.</summary>
/// <param name="SourcePath">The file on disk.</param>
/// <param name="EntryName">The entry's path inside the archive.</param>
public readonly record struct SkillArchiveEntry(string SourcePath, string EntryName);

/// <summary>Writes the .zip bundle of one skill.</summary>
public interface ISkillArchiveWriter
{
    /// <summary>Creates <paramref name="output"/> holding <paramref name="entries"/>, in order.</summary>
    /// <param name="output">The archive to create; it must not exist.</param>
    /// <param name="entries">The files to bundle, SKILL.md first.</param>
    void Write(string output, IReadOnlyList<SkillArchiveEntry> entries);
}
