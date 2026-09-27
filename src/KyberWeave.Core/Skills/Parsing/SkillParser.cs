using KyberWeave.Core.Parsing;
using KyberWeave.Core.Skills.Model;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
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
        new MarkdownPipelineBuilder { TrackTrivia = true }.UseYamlFrontMatter().Build();

    private static readonly IDeserializer Deserializer =
        new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "license", "compatibility", "metadata", "allowed-tools"
    };

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
        List<SkillReferenceLink> links = ExtractReferenceLinks(document, directoryPath);
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
        List<string> lines = slice.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("---", StringComparison.Ordinal)) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("---", StringComparison.Ordinal)) lines.RemoveAt(lines.Count - 1);
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
                if (entry.Key is YamlScalarNode { Value: { } keyName } && !KnownKeys.Contains(keyName))
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

    private static List<SkillReferenceLink> ExtractReferenceLinks(MarkdownDocument document, string directoryPath)
    {
        IReadOnlyList<ExtractedFileReference> extracted = FileReferenceExtractor.ExtractFromDocument(document, directoryPath, FileReferenceOptions.SkillDefault);
        return extracted.Select(r => new SkillReferenceLink(r.Reference, r.Exists)).ToList();
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
#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
        string top = relativePath.Split('/')[0].ToLowerInvariant();
#pragma warning restore CA1308
        return top switch
        {
            "scripts" => SkillResourceKind.Script,
            "references" => SkillResourceKind.Reference,
            "assets" => SkillResourceKind.Asset,
            _ => SkillResourceKind.Other
        };
    }
}
