using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using KyberWeave.Core.Skills.Model;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KyberWeave.Core.Skills.Parsing;

/// <summary>
/// Parses a single SKILL.md file into a <see cref="Skill"/>. Uses Markdig with the
/// YAML front matter extension to split front matter from body, YamlDotNet to
/// deserialize the front matter, and a raw YAML pass to capture unknown keys.
/// </summary>
public static class SkillParser
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
        new(@"(?<![A-Za-z0-9._\-/])(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)", RegexOptions.Compiled);

    public static Skill ParseFile(string skillFilePath)
    {
        if (!File.Exists(skillFilePath))
            throw new SkillParseException($"SKILL.md not found at '{skillFilePath}'.");

        var raw = File.ReadAllText(skillFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(skillFilePath))!;
        return Parse(raw, Path.GetFullPath(skillFilePath), directory);
    }

    /// <summary>Parse from in-memory content (used by tests). Resource discovery still runs against <paramref name="directoryPath"/> if it exists.</summary>
    public static Skill Parse(string content, string skillFilePath, string directoryPath)
    {
        var document = Markdown.Parse(content, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault()
            ?? throw new SkillParseException("No YAML front matter found. A SKILL.md must begin with a '---' fenced YAML block.");

        var rawYaml = ExtractYamlText(content, yamlBlock);

        SkillFrontmatter frontmatter;
        try
        {
            frontmatter = Deserializer.Deserialize<SkillFrontmatter>(rawYaml) ?? new SkillFrontmatter();
        }
        catch (Exception ex)
        {
            throw new SkillParseException($"Front matter is not valid YAML: {ex.Message}", ex);
        }

        CaptureUnknownKeys(rawYaml, frontmatter);

        var body = ExtractBody(content, yamlBlock);
        var links = ExtractReferenceLinks(document, body, directoryPath);
        var resources = DiscoverResources(directoryPath, skillFilePath);

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
        var slice = content.Substring(block.Span.Start, block.Span.Length);
        var lines = slice.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("---")) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("---")) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static string ExtractBody(string content, YamlFrontMatterBlock block)
    {
        var afterIndex = block.Span.End + 1;
        if (afterIndex >= content.Length) return string.Empty;
        return content.Substring(afterIndex).TrimStart('\r', '\n');
    }

    private static void CaptureUnknownKeys(string rawYaml, SkillFrontmatter frontmatter)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(rawYaml));
            if (stream.Documents.Count == 0) return;
            if (stream.Documents[0].RootNode is not YamlMappingNode root) return;

            foreach (var entry in root.Children)
            {
                if (entry.Key is YamlScalarNode key && key.Value is { } keyName && !KnownKeys.Contains(keyName))
                {
                    var value = entry.Value is YamlScalarNode sv ? sv.Value ?? string.Empty : "<complex>";
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
        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        void Consider(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            if (target.StartsWith("http://") || target.StartsWith("https://") || target.StartsWith('#') || target.StartsWith("mailto:"))
                return;
            var normalized = target.Trim();
            if (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }
            var resolves = ResolvesOnDisk(directoryPath, normalized);
            found[normalized] = resolves;
        }

        foreach (var link in document.Descendants<LinkInline>())
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
            var full = Path.GetFullPath(Path.Combine(directoryPath, relative));
            var baseFull = Path.GetFullPath(directoryPath);
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
        var resources = new List<SkillResource>();
        if (!Directory.Exists(directoryPath)) return resources;

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(file), skillFilePath, StringComparison.Ordinal)) continue;
            var rel = Path.GetRelativePath(directoryPath, file).Replace('\\', '/');
            var kind = ClassifyResource(rel);
            resources.Add(new SkillResource(rel, Path.GetFullPath(file), kind));
        }
        return resources;
    }

    private static SkillResourceKind ClassifyResource(string relativePath)
    {
        var top = relativePath.Split('/')[0].ToLowerInvariant();
        return top switch
        {
            "scripts" => SkillResourceKind.Script,
            "references" => SkillResourceKind.Reference,
            "assets" => SkillResourceKind.Asset,
            _ => SkillResourceKind.Other
        };
    }
}
