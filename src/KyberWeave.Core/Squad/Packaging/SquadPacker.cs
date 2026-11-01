using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>
/// Packages canonical Kyber-Squad source into deterministic APM and Agent Plugins archives.
/// </summary>
public static class SquadPacker
{
    private const string PackTarget = "pack";
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Creates the APM distribution archive containing canonical manifests, profiles, agents, and skills. The skills
    /// under the optional companion root (<c>.apm/skills</c>) are packed beside the Squad skills.
    /// </summary>
    public static string PackApm(
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

        Directory.CreateDirectory(outDirectory);
        WriteArchive(archivePath, entries);
        return archivePath;
    }

    /// <summary>
    /// Creates the Agent Plugins v1.0.0 distribution archive containing canonical skills, MCP configuration, and plugin manifest.
    /// The companion skills take the same skill trees as the APM archive, and add no agents.
    /// </summary>
    public static string PackPlugins(
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

        Directory.CreateDirectory(outDirectory);
        WriteArchive(archivePath, entries);
        return archivePath;
    }

    /// <summary>
    /// Creates both APM and Agent Plugins distribution archives and writes SHA256SUMS.txt.
    /// </summary>
    public static (string ApmArchivePath, string PluginsArchivePath, string ChecksumFilePath) PackAll(
        string sourcePath,
        string outDirectory,
        string version,
        string? companionSkillsRoot = null)
    {
        string apmArchive = PackApm(sourcePath, outDirectory, version, companionSkillsRoot);
        string pluginsArchive = PackPlugins(sourcePath, outDirectory, version, companionSkillsRoot);

        string apmSha256 = ComputeSha256(File.ReadAllBytes(apmArchive));
        string pluginsSha256 = ComputeSha256(File.ReadAllBytes(pluginsArchive));

        string apmFileName = Path.GetFileName(apmArchive);
        string pluginsFileName = Path.GetFileName(pluginsArchive);

        string checksumContent = $"{apmSha256}  {apmFileName}\n{pluginsSha256}  {pluginsFileName}\n";
        string checksumFilePath = Path.Combine(outDirectory, "SHA256SUMS.txt");
        File.WriteAllText(checksumFilePath, checksumContent, Utf8NoBom);

        return (apmArchive, pluginsArchive, checksumFilePath);
    }

    private static List<(string RelativePath, byte[] Content)> CollectApmEntries(
        string sourcePath,
        string? companionSkillsRoot)
    {
        List<(string RelativePath, byte[] Content)> entries = [];

        foreach (string filePath in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            string relPath = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');

            // Exclude temporary or git or test artifacts if any
            if (relPath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith("migration/", StringComparison.OrdinalIgnoreCase))
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

    private static List<(string RelativePath, byte[] Content)> CollectPluginsEntries(
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
        if (File.Exists(mcpPath))
        {
            entries.Add(("mcp.json", ReadNormalizedFile(mcpPath)));
        }

        // 3. Include skills/
        string skillsDir = Path.Combine(sourcePath, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (string filePath in Directory.GetFiles(skillsDir, "*", SearchOption.AllDirectories))
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
    private static List<(string RelativePath, byte[] Content)> CollectCompanionEntries(
        string sourcePath,
        string? companionSkillsRoot)
    {
        return SquadCompanionSkills.Collect(sourcePath, companionSkillsRoot)
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
        List<SquadDeploymentFile> files = entries
            .Select(entry => new SquadDeploymentFile(entry.RelativePath, entry.Content, PackTarget))
            .ToList();

        SquadRenderResult filled = SquadResponsePreferencesInjector.Inject(
            new SquadRenderResult(true, files, [], [], []),
            SquadResponseGuidanceSource.DefaultText);

        return filled.Files
            .Select(file => (file.RelativePath, file.Content.ToArray()))
            .ToList();
    }

    private static byte[] ReadNormalizedFile(string filePath)
    {
        if (IsTextFile(filePath))
        {
            string text = File.ReadAllText(filePath, Encoding.UTF8);
            string normalized = NormalizeLineEndings(text);
            return Utf8NoBom.GetBytes(normalized);
        }

        return File.ReadAllBytes(filePath);
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
    /// Writes one archive at <paramref name="archivePath"/>. The entries go to a temporary file beside the target, which
    /// replaces the target only once the write is complete. A failed write therefore leaves the previous archive in place
    /// and no partial zip. <paramref name="beforeEntry"/> runs before each entry is written; it exists so a test can fail
    /// a write part way through, as a full disk would.
    /// </summary>
    internal static void WriteArchive(
        string archivePath,
        List<(string RelativePath, byte[] Content)> entries,
        Action<string>? beforeEntry = null)
    {
        string temporaryPath = $"{archivePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            WriteDeterministicZip(temporaryPath, entries, beforeEntry);
            File.Move(temporaryPath, archivePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteDeterministicZip(
        string zipPath,
        List<(string RelativePath, byte[] Content)> entries,
        Action<string>? beforeEntry)
    {
        using FileStream fileStream = new(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Create, leaveOpen: false);

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
