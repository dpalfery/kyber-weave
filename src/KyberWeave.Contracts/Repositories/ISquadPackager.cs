namespace KyberWeave.Contracts.Repositories;

/// <summary>Writes the Squad distribution archives from the canonical source.</summary>
public interface ISquadPackager
{
    /// <summary>Loads the Squad source tree to prove it is valid, discarding the result.</summary>
    /// <param name="sourcePath">The <c>products/kyber-squad</c> directory.</param>
    /// <exception cref="InvalidDataException">The source is refused.</exception>
    void ValidateSource(string sourcePath);

    /// <summary>Resolves the companion skills root beside the Squad source.</summary>
    /// <param name="sourcePath">The <c>products/kyber-squad</c> directory.</param>
    /// <returns>The companion skills root, or null when there is none.</returns>
    string? ResolveCompanionSkillsRoot(string sourcePath);

    /// <summary>
    /// Packs the APM archive into a scratch folder, extracts it and loads it, so the names and bytes install will see are checked.
    /// Nothing reaches the output folder, and the scratch folder is removed on every path.
    /// </summary>
    /// <param name="sourcePath">The <c>products/kyber-squad</c> directory.</param>
    /// <param name="companionSkillsRoot">The companion skills root, or null.</param>
    /// <param name="version">The version to pack under.</param>
    /// <param name="warn">Receives a warning when the scratch folder could not be removed.</param>
    void ValidateComposedTree(string sourcePath, string? companionSkillsRoot, string version, Action<string> warn);

    /// <summary>Writes the APM archive.</summary>
    /// <param name="sourcePath">The <c>products/kyber-squad</c> directory.</param>
    /// <param name="outDirectory">Where the archive goes.</param>
    /// <param name="version">The version to pack under.</param>
    /// <param name="companionSkillsRoot">The companion skills root, or null.</param>
    /// <returns>The archive path.</returns>
    string PackApm(string sourcePath, string outDirectory, string version, string? companionSkillsRoot);

    /// <summary>Writes the Agent Plugins archive.</summary>
    /// <param name="sourcePath">The <c>products/kyber-squad</c> directory.</param>
    /// <param name="outDirectory">Where the archive goes.</param>
    /// <param name="version">The version to pack under.</param>
    /// <param name="companionSkillsRoot">The companion skills root, or null.</param>
    /// <returns>The archive path.</returns>
    string PackPlugins(string sourcePath, string outDirectory, string version, string? companionSkillsRoot);

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
        string? companionSkillsRoot);
}
