using System.IO.Compression;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>Writes the Squad archives through <see cref="ISquadArchiveWriter"/>, and checks the composed tree in a scratch folder.</summary>
/// <param name="archives">Writes the archives from the canonical source.</param>
/// <param name="loader">Loads and validates the source trees.</param>
/// <param name="companions">Finds the companion skills beside the source.</param>
/// <param name="files">Holds the scratch folder the composed tree is extracted into.</param>
/// <param name="environment">Names the temporary directory the scratch folder lives under.</param>
/// <param name="ids">Names the scratch folder.</param>
public sealed class FileSquadPackager(
    ISquadArchiveWriter archives,
    ISquadSourceLoader loader,
    ISquadCompanionSkills companions,
    IWritableFileSystem files,
    IProcessEnvironment environment,
    IIdGenerator ids) : ISquadPackager
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

        string scratch = Path.Combine(environment.TempDirectory, $"kyber-squad-pack-{ids.NewGuid():N}");
        try
        {
            string archive = archives.PackApm(sourcePath, scratch, version, companionSkillsRoot);
            string tree = Path.Combine(scratch, "tree");
            ExtractArchive(archive, tree);
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

    /// <summary>Extracts an archive into a directory through the file-system seam, refusing an entry that would land outside it.</summary>
    private void ExtractArchive(string archivePath, string destination)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        files.CreateDirectory(destination);
        using ZipArchive zip = new(new MemoryStream(files.ReadAllBytes(archivePath)));
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new IOException($"Extracting the archive entry '{entry.FullName}' would place it outside '{destination}'.");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                files.CreateDirectory(target);
                continue;
            }

            string? parent = Path.GetDirectoryName(target);
            if (parent is not null)
            {
                files.CreateDirectory(parent);
            }

            using Stream source = entry.Open();
            using MemoryStream buffer = new();
            source.CopyTo(buffer);
            files.WriteAllBytes(target, buffer.ToArray());
        }
    }

    /// <summary>
    /// Removes the scratch folder without letting a cleanup failure replace the pack's own result or error. A folder
    /// that could not be removed is reported as a warning, so it is not silently left behind.
    /// </summary>
    private void RemoveScratch(string scratch, Action<string> warn)
    {
        try
        {
            if (files.DirectoryExists(scratch))
            {
                files.DeleteTreeNoFollow(scratch);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"could not remove scratch folder {scratch}: {ex.Message}");
        }
    }
}
