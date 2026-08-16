using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>
/// Packages canonical Kyber-Squad source into deterministic APM and Agent Plugins archives.
/// </summary>
public static class SquadPacker
{
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Creates the APM distribution archive containing canonical manifests, profiles, agents, and skills.
    /// </summary>
    public static string PackApm(string sourcePath, string outDirectory, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        Directory.CreateDirectory(outDirectory);
        string archivePath = Path.Combine(outDirectory, $"kyber-squad-{version}.zip");

        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        List<(string RelativePath, byte[] Content)> entries = CollectApmEntries(sourcePath);
        entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        WriteDeterministicZip(archivePath, entries);
        return archivePath;
    }

    /// <summary>
    /// Creates the Agent Plugins v1.0.0 distribution archive containing canonical skills, MCP configuration, and plugin manifest.
    /// </summary>
    public static string PackPlugins(string sourcePath, string outDirectory, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        Directory.CreateDirectory(outDirectory);
        string archivePath = Path.Combine(outDirectory, $"kyber-squad-plugin-{version}.zip");

        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        List<(string RelativePath, byte[] Content)> entries = CollectPluginsEntries(sourcePath, version);
        entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        WriteDeterministicZip(archivePath, entries);
        return archivePath;
    }

    /// <summary>
    /// Creates both APM and Agent Plugins distribution archives and writes SHA256SUMS.txt.
    /// </summary>
    public static (string ApmArchivePath, string PluginsArchivePath, string ChecksumFilePath) PackAll(
        string sourcePath,
        string outDirectory,
        string version)
    {
        string apmArchive = PackApm(sourcePath, outDirectory, version);
        string pluginsArchive = PackPlugins(sourcePath, outDirectory, version);

        string apmSha256 = ComputeSha256(File.ReadAllBytes(apmArchive));
        string pluginsSha256 = ComputeSha256(File.ReadAllBytes(pluginsArchive));

        string apmFileName = Path.GetFileName(apmArchive);
        string pluginsFileName = Path.GetFileName(pluginsArchive);

        string checksumContent = $"{apmSha256}  {apmFileName}\n{pluginsSha256}  {pluginsFileName}\n";
        string checksumFilePath = Path.Combine(outDirectory, "SHA256SUMS.txt");
        File.WriteAllText(checksumFilePath, checksumContent, Utf8NoBom);

        return (apmArchive, pluginsArchive, checksumFilePath);
    }

    private static List<(string RelativePath, byte[] Content)> CollectApmEntries(string sourcePath)
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
                relPath.StartsWith(".gemini/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".opencode/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".kilo/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".warp/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".factory/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] content = ReadNormalizedFile(filePath);
            entries.Add((relPath, content));
        }

        return entries;
    }

    private static List<(string RelativePath, byte[] Content)> CollectPluginsEntries(string sourcePath, string version)
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

        return entries;
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
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static bool IsTextFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".md" or ".yml" or ".yaml" or ".json" or ".toml" or ".txt" or ".ps1" or ".sh";
    }

    private static void WriteDeterministicZip(string zipPath, List<(string RelativePath, byte[] Content)> entries)
    {
        using FileStream fileStream = new(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        foreach ((string relPath, byte[] content) in entries)
        {
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
