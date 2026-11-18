namespace KyberWeave.Contracts.Repositories;

/// <summary>Writes the Squad distribution archives from a canonical source tree.</summary>
/// <remarks>
/// Separate from <see cref="ISquadPackager"/>: a packer also checks that the composed tree loads, which needs the
/// Squad source loader. This port is only the archive writing, so the logic that builds an archive can sit above
/// the file-system seam and be tested against an in-memory tree.
/// </remarks>
public interface ISquadArchiveWriter
{
    /// <summary>Writes the APM archive.</summary>
    /// <param name="sourcePath">The <c>products/kyber-squad</c> directory.</param>
    /// <param name="outDirectory">Where the archive goes.</param>
    /// <param name="version">The version to pack under.</param>
    /// <param name="companionSkillsRoot">The companion skills root, or null.</param>
    /// <returns>The archive path.</returns>
    string PackApm(string sourcePath, string outDirectory, string version, string? companionSkillsRoot = null);

    /// <summary>Writes the Agent Plugins archive.</summary>
    /// <param name="sourcePath">The <c>products/kyber-squad</c> directory.</param>
    /// <param name="outDirectory">Where the archive goes.</param>
    /// <param name="version">The version to pack under.</param>
    /// <param name="companionSkillsRoot">The companion skills root, or null.</param>
    /// <returns>The archive path.</returns>
    string PackPlugins(string sourcePath, string outDirectory, string version, string? companionSkillsRoot = null);

    /// <summary>Writes both archives and their checksum file.</summary>
    /// <param name="sourcePath">The <c>products/kyber-squad</c> directory.</param>
    /// <param name="outDirectory">Where the archives go.</param>
    /// <param name="version">The version to pack under.</param>
    /// <param name="companionSkillsRoot">The companion skills root, or null.</param>
    /// <returns>The APM archive, the Agent Plugins archive and the checksum file.</returns>
    (string ApmArchivePath, string PluginsArchivePath, string ChecksumFilePath) PackAll(
        string sourcePath,
        string outDirectory,
        string version,
        string? companionSkillsRoot = null);
}
