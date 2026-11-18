using System.IO.Compression;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>Writes the Squad archives through <see cref="ISquadArchiveWriter"/>, and checks the composed tree in a scratch folder.</summary>
/// <param name="archives">Writes the archives from the canonical source.</param>
/// <param name="loader">Loads and validates the source trees.</param>
/// <param name="companions">Finds the companion skills beside the source.</param>
public sealed class FileSquadPackager(
    ISquadArchiveWriter archives,
    ISquadSourceLoader loader,
    ISquadCompanionSkills companions) : ISquadPackager
{
    /// <inheritdoc />
    public void ValidateSource(string sourcePath)
    {
        // The real Squad tree is loaded before anything is composed or written. The loader's symbolic-link guards
        // run only on the files it reads, so loading the staged copy alone would never see a link out of the source.
        _ = loader.Load(sourcePath);
    }

    /// <inheritdoc />
    public string? ResolveCompanionSkillsRoot(string sourcePath) => companions.ResolveRoot(sourcePath);

    /// <inheritdoc />
    public void ValidateComposedTree(string sourcePath, string? companionSkillsRoot, string version, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);

        string scratch = Path.Combine(Path.GetTempPath(), $"kyber-squad-pack-{Guid.NewGuid():N}");
        try
        {
            string archive = archives.PackApm(sourcePath, scratch, version, companionSkillsRoot);
            string tree = Path.Combine(scratch, "tree");
            ZipFile.ExtractToDirectory(archive, tree);
            _ = loader.Load(tree);
        }
        finally
        {
            RemoveScratch(scratch, warn);
        }
    }

    /// <inheritdoc />
    public string PackApm(string sourcePath, string outDirectory, string version, string? companionSkillsRoot) =>
        archives.PackApm(sourcePath, outDirectory, version, companionSkillsRoot);

    /// <inheritdoc />
    public string PackPlugins(string sourcePath, string outDirectory, string version, string? companionSkillsRoot) =>
        archives.PackPlugins(sourcePath, outDirectory, version, companionSkillsRoot);

    /// <inheritdoc />
    public (string ApmArchivePath, string PluginsArchivePath, string ChecksumFilePath) PackAll(
        string sourcePath,
        string outDirectory,
        string version,
        string? companionSkillsRoot) =>
        archives.PackAll(sourcePath, outDirectory, version, companionSkillsRoot);

    /// <summary>
    /// Removes the scratch folder without letting a cleanup failure replace the pack's own result or error. A folder
    /// that could not be removed is reported as a warning, so it is not silently left behind.
    /// </summary>
    private static void RemoveScratch(string scratch, Action<string> warn)
    {
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"could not remove scratch folder {scratch}: {ex.Message}");
        }
    }
}
