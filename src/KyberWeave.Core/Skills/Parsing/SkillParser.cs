using System.Text.RegularExpressions;
using KyberWeave.Core.Skills.Model;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KyberWeave.Core.Skills.Parsing;

/// <summary>
/// Parses a single SKILL.md file into a <see cref="Skill"/>. Uses Markdig with the
/// YAML front matter extension to split front matter from body, YamlDotNet to
/// deserialize the front matter, and a raw YAML pass to capture unknown keys.
/// </summary>
public static partial class SkillParser
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseYamlFrontMatter().Build();

    private static readonly IDeserializer Deserializer =
        new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "license", "compatibility", "metadata", "allowed-tools"
    };

    private static readonly Regex InlinePathRegex =
        MyRegex();

    public static Skill ParseFile(string skillFilePath)
    {
        if (!File.Exists(skillFilePath))
            throw new SkillParseException($"SKILL.md not found at '{skillFilePath}'.");

        string raw = File.ReadAllText(skillFilePath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(skillFilePath))!;
        return Parse(raw, Path.GetFullPath(skillFilePath), directory);
    }

    /// <summary>Parse from in-memory content (used by tests). Resource discovery still runs against <paramref name="directoryPath"/> if it exists.</summary>
    public static Skill Parse(string content, string skillFilePath, string directoryPath)
    {
        MarkdownDocument document = Markdown.Parse(content, Pipeline);
        YamlFrontMatterBlock yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault()
            ?? throw new SkillParseException("No YAML front matter found. A SKILL.md must begin with a '---' fenced YAML block.");

        string rawYaml = ExtractYamlText(content, yamlBlock);

        SkillFrontmatter frontmatter;
        try
        {
            frontmatter = Deserializer.Deserialize<SkillFrontmatter?>(rawYaml) ?? new SkillFrontmatter();
        }
        catch (Exception ex)
        {
            throw new SkillParseException($"Front matter is not valid YAML: {ex.Message}", ex);
        }

        CaptureUnknownKeys(rawYaml, frontmatter);

        string body = ExtractBody(content, yamlBlock);
        List<SkillReferenceLink> links = ExtractReferenceLinks(document, body, directoryPath);
        List<SkillResource> resources = DiscoverResources(directoryPath, skillFilePath);

        return new Skill
        {
            SkillFilePath = skillFilePath,
            DirectoryPath = directoryPath,
            Frontmatter = frontmatter,
            RawFrontmatter = rawYaml,
            InstructionsBody = body,
            ReferenceLinks = links,
            Resources = resources
        };
    }

    private static string ExtractYamlText(string content, YamlFrontMatterBlock block)
    {
        // The block span includes the --- fences; strip leading/trailing fence lines.
        string slice = content.Substring(block.Span.Start, block.Span.Length);
        List<string> lines = slice.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("---")) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("---")) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static string ExtractBody(string content, YamlFrontMatterBlock block)
    {
        int afterIndex = block.Span.End + 1;
        if (afterIndex >= content.Length) return string.Empty;
        return content.Substring(afterIndex).TrimStart('\r', '\n');
    }

    private static void CaptureUnknownKeys(string rawYaml, SkillFrontmatter frontmatter)
    {
        try
        {
            YamlStream stream = new YamlStream();
            stream.Load(new StringReader(rawYaml));
            if (stream.Documents.Count == 0) return;
            if (stream.Documents[0].RootNode is not YamlMappingNode root) return;

            foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
            {
                if (entry.Key is YamlScalarNode key && key.Value is { } keyName && !KnownKeys.Contains(keyName))
                {
                    string value = entry.Value is YamlScalarNode sv ? sv.Value ?? string.Empty : "<complex>";
                    frontmatter.UnknownKeys[keyName] = value;
                }
            }
        }
        catch
        {
            // Best-effort; deserialize already validated the YAML shape.
        }
    }

    private static List<SkillReferenceLink> ExtractReferenceLinks(MarkdownDocument document, string body, string directoryPath)
    {
        Dictionary<string, bool> found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        void Consider(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            if (target.StartsWith("http://") || target.StartsWith("https://") || target.StartsWith('#') || target.StartsWith("mailto:"))
                return;
            string normalized = target.Trim();
            if (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }
            bool resolves = ResolvesOnDisk(directoryPath, normalized);
            found[normalized] = resolves;
        }

        foreach (LinkInline link in document.Descendants<LinkInline>())
            if (link.Url is { } url) Consider(url);

        foreach (Match m in InlinePathRegex.Matches(body))
            Consider(m.Groups["path"].Value);

        return found.Select(kv => new SkillReferenceLink(kv.Key, kv.Value)).ToList();
    }

    private static bool ResolvesOnDisk(string directoryPath, string relative)
    {
        try
        {
            if (relative.Contains("..")) return false; // traversal: treat as unresolved/suspicious
            string full = Path.GetFullPath(Path.Combine(directoryPath, relative));
            string baseFull = Path.GetFullPath(directoryPath);
            if (!full.StartsWith(baseFull, StringComparison.Ordinal)) return false;
            return File.Exists(full) || Directory.Exists(full);
        }
        catch
        {
            return false;
        }
    }

    private static List<SkillResource> DiscoverResources(string directoryPath, string skillFilePath)
    {
        List<SkillResource> resources = new List<SkillResource>();
        if (!Directory.Exists(directoryPath)) return resources;

        foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(file), skillFilePath, StringComparison.Ordinal)) continue;
            string rel = Path.GetRelativePath(directoryPath, file).Replace('\\', '/');
            SkillResourceKind kind = ClassifyResource(rel);
            resources.Add(new SkillResource(rel, Path.GetFullPath(file), kind));
        }
        return resources;
    }

    private static SkillResourceKind ClassifyResource(string relativePath)
    {
        string top = relativePath.Split('/')[0].ToLowerInvariant();
        return top switch
        {
            "scripts" => SkillResourceKind.Script,
            "references" => SkillResourceKind.Reference,
            "assets" => SkillResourceKind.Asset,
            _ => SkillResourceKind.Other
        };
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9._\-/])(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
