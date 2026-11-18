using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>
/// Packages canonical Kyber-Squad source into deterministic APM and Agent Plugins archives.
/// </summary>
/// <param name="files">Reads the source tree and writes the archives.</param>
/// <param name="companionSkills">Lists the companion skills shipped beside the source tree.</param>
public sealed class SquadPacker(IWritableFileSystem files, ISquadCompanionSkills companionSkills) : ISquadArchiveWriter
{
    private const string PackTarget = "pack";
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    /// <inheritdoc />
    public string PackApm(
        string sourcePath,
        string outDirectory,
        string version,
        string? companionSkillsRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        string archivePath = Path.Combine(outDirectory, $"kyber-squad-{version}.zip");

        // Collect before touching the output folder, so a refused pack keeps the last good archive at this path.
        List<(string RelativePath, byte[] Content)> entries = FillResponsePreferences(
            CollectApmEntries(sourcePath, companionSkillsRoot));
        entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        files.CreateDirectory(outDirectory);
        WriteArchive(archivePath, entries);
        return archivePath;
    }

    /// <inheritdoc />
    public string PackPlugins(
        string sourcePath,
        string outDirectory,
        string version,
        string? companionSkillsRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        string archivePath = Path.Combine(outDirectory, $"kyber-squad-plugin-{version}.zip");

        // Collect before touching the output folder, so a refused pack keeps the last good archive at this path.
        List<(string RelativePath, byte[] Content)> entries = FillResponsePreferences(
            CollectPluginsEntries(sourcePath, version, companionSkillsRoot));
        entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        files.CreateDirectory(outDirectory);
        WriteArchive(archivePath, entries);
        return archivePath;
    }

    /// <inheritdoc />
    public (string ApmArchivePath, string PluginsArchivePath, string ChecksumFilePath) PackAll(
        string sourcePath,
        string outDirectory,
        string version,
        string? companionSkillsRoot = null)
    {
        string apmArchive = PackApm(sourcePath, outDirectory, version, companionSkillsRoot);
        string pluginsArchive = PackPlugins(sourcePath, outDirectory, version, companionSkillsRoot);

        string apmSha256 = ComputeSha256(files.ReadAllBytes(apmArchive));
        string pluginsSha256 = ComputeSha256(files.ReadAllBytes(pluginsArchive));

        string apmFileName = Path.GetFileName(apmArchive);
        string pluginsFileName = Path.GetFileName(pluginsArchive);

        string checksumContent = $"{apmSha256}  {apmFileName}\n{pluginsSha256}  {pluginsFileName}\n";
        string checksumFilePath = Path.Combine(outDirectory, "SHA256SUMS.txt");
        files.WriteAllText(checksumFilePath, checksumContent);

        return (apmArchive, pluginsArchive, checksumFilePath);
    }

    private List<(string RelativePath, byte[] Content)> CollectApmEntries(
        string sourcePath,
        string? companionSkillsRoot)
    {
        List<(string RelativePath, byte[] Content)> entries = [];

        foreach (string filePath in EnumerateTree(sourcePath))
        {
            string relPath = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');

            // Exclude temporary or git or test artifacts if any
            if (relPath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Exclude target-specific trees
            if (relPath.StartsWith(".codex/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".cursor/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".opencode/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".kilo/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".warp/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".factory/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".pi/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".zcode/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".devin/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".commandcode/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] content = ReadNormalizedFile(filePath);
            entries.Add((relPath, content));
        }

        entries.AddRange(CollectCompanionEntries(sourcePath, companionSkillsRoot));
        return entries;
    }

    private List<(string RelativePath, byte[] Content)> CollectPluginsEntries(
        string sourcePath,
        string version,
        string? companionSkillsRoot)
    {
        List<(string RelativePath, byte[] Content)> entries = [];

        // 1. Synthesize conformant plugin.json
        string pluginJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["$schema"] = "https://agent-plugins.org/specification/v1/schema.json",
            ["name"] = "kyber-squad",
            ["version"] = version,
            ["description"] = "Kyber-Squad canonical skills and MCP tools.",
            ["skills"] = "./skills",
            ["mcp"] = "./mcp.json"
        }, IndentedJsonOptions);

        pluginJson = NormalizeLineEndings(pluginJson);
        entries.Add(("plugin.json", Utf8NoBom.GetBytes(pluginJson)));

        // 2. Include mcp.json
        string mcpPath = Path.Combine(sourcePath, "mcp.json");
        if (files.FileExists(mcpPath))
        {
            entries.Add(("mcp.json", ReadNormalizedFile(mcpPath)));
        }

        // 3. Include skills/
        string skillsDir = Path.Combine(sourcePath, "skills");
        if (files.DirectoryExists(skillsDir))
        {
            foreach (string filePath in EnumerateTree(skillsDir))
            {
                string relPath = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');
                entries.Add((relPath, ReadNormalizedFile(filePath)));
            }
        }

        entries.AddRange(CollectCompanionEntries(sourcePath, companionSkillsRoot));
        return entries;
    }

    /// <summary>
    /// The companion skills as archive entries. They are collected with the Squad entries, so the sort and the
    /// response-preferences fill see one list, and an archive stays a function of its inputs.
    /// </summary>
    private List<(string RelativePath, byte[] Content)> CollectCompanionEntries(
        string sourcePath,
        string? companionSkillsRoot)
    {
        return companionSkills.Collect(sourcePath, companionSkillsRoot)
            .Select(file => (file.RelativePath, ReadNormalizedFile(file.SourcePath)))
            .ToList();
    }

    /// <summary>
    /// Fills every carrier's response-preferences block with the shipped default text. The packer
    /// never consults the operator's home guidance: the archive is a function of the canonical
    /// source alone. A corpus with no block is passed through unchanged (injector no-op).
    /// </summary>
    /// <remarks>
    ///     Packing uses the shipped default text and never reads the operator's home guidance.
    /// </remarks>
    private static List<(string RelativePath, byte[] Content)> FillResponsePreferences(
        List<(string RelativePath, byte[] Content)> entries)
    {
        List<SquadDeploymentFile> filesToFill = entries
            .Select(entry => new SquadDeploymentFile(entry.RelativePath, entry.Content, PackTarget))
            .ToList();

        SquadRenderResult filled = SquadResponsePreferencesInjector.Inject(
            new SquadRenderResult(true, filesToFill, [], [], []),
            SquadResponseGuidanceSource.DefaultText);

        return filled.Files
            .Select(file => (file.RelativePath, file.Content.ToArray()))
            .ToList();
    }

    /// <summary>
    /// Every file under a directory, at any depth. It walks the entries rather than the file-system seam's file
    /// listing, because that listing skips build output and dependency folders: an archive is a function of the
    /// source tree, so a folder named <c>out</c> or <c>dist</c> in it must still be packed.
    /// </summary>
    private IEnumerable<string> EnumerateTree(string directory)
    {
        foreach (FileSystemEntry entry in files.EnumerateEntries(directory))
        {
            if (entry.IsDirectory)
            {
                foreach (string file in EnumerateTree(entry.Path))
                {
                    yield return file;
                }
            }
            else
            {
                yield return entry.Path;
            }
        }
    }

    private byte[] ReadNormalizedFile(string filePath)
    {
        if (IsTextFile(filePath))
        {
            string text = files.ReadAllText(filePath);
            string normalized = NormalizeLineEndings(text);
            return Utf8NoBom.GetBytes(normalized);
        }

        return files.ReadAllBytes(filePath);
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
    private static bool IsTextFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
#pragma warning restore CA1308
        return ext is ".md" or ".yml" or ".yaml" or ".json" or ".toml" or ".txt" or ".ps1" or ".sh";
    }

    /// <summary>
    /// Writes one archive at <paramref name="archivePath"/>. The archive is composed in memory and then written as
    /// one atomic replacement of the target, so a failed write leaves the previous archive in place and no partial
    /// zip. <paramref name="beforeEntry"/> runs before each entry is written; it exists so a test can fail a write
    /// part way through, as a full disk would.
    /// </summary>
    public void WriteArchive(
        string archivePath,
        IList<(string RelativePath, byte[] Content)> entries,
        Action<string>? beforeEntry = null)
    {
        using MemoryStream buffer = new();
        WriteDeterministicZip(buffer, entries, beforeEntry);
        files.WriteAllBytesAtomic(archivePath, buffer.ToArray());
    }

    private static void WriteDeterministicZip(
        Stream stream,
        IList<(string RelativePath, byte[] Content)> entries,
        Action<string>? beforeEntry)
    {
        using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true);

        foreach ((string relPath, byte[] content) in entries)
        {
            beforeEntry?.Invoke(relPath);
            ZipArchiveEntry entry = archive.CreateEntry(relPath, CompressionLevel.Optimal);
            entry.LastWriteTime = FixedTimestamp;

            using Stream entryStream = entry.Open();
            entryStream.Write(content, 0, content.Length);
        }
    }

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
