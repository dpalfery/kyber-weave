using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KyberWeave.Core.Parsing;

/// <summary>
/// The result of reading a Markdown file's YAML frontmatter.
/// </summary>
/// <param name="HasFrontmatter">True when a frontmatter block was present.</param>
/// <param name="Yaml">The raw YAML text with its delimiters removed.</param>
/// <param name="Body">The Markdown body following the frontmatter block.</param>
/// <param name="BodyStartLine">The 1-based source line on which <paramref name="Body"/> begins.</param>
public readonly record struct FrontmatterReadResult(
    bool HasFrontmatter,
    string Yaml,
    string Body,
    int BodyStartLine = 1);

/// <summary>
/// Format-agnostic reader for YAML frontmatter in Markdown files.
/// </summary>
/// <remarks>
/// Extracted from the agent parser so that every artifact class — skills, agent
/// definitions, and documentation — shares one frontmatter implementation. The Markdig
/// pipeline and the YamlDotNet naming convention are preserved exactly as the agent
/// parser configured them; changing either would silently alter how existing agent
/// manifests deserialize.
/// </remarks>
public static class MarkdownFrontmatterReader
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseYamlFrontMatter().Build();

    /// <summary>
    /// Shared deserializer. Hyphenated naming convention: a <c>doc-type</c> key binds to
    /// a <c>DocType</c> property. Unmatched properties are ignored so that adding a key
    /// to a document does not break parsing.
    /// </summary>
    public static readonly IDeserializer Deserializer =
        new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    /// <summary>Reads frontmatter and body from raw Markdown content.</summary>
    public static FrontmatterReadResult Read(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        MarkdownDocument document = Markdown.Parse(content, Pipeline);
        YamlFrontMatterBlock? block = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();

        if (block is null)
        {
            return new FrontmatterReadResult(false, string.Empty, content);
        }

        (string? body, int bodyStartLine) = ExtractBody(content, block);
        return new FrontmatterReadResult(true, ExtractYaml(content, block), body, bodyStartLine);
    }

    /// <summary>Reads frontmatter and body from a file on disk.</summary>
    public static FrontmatterReadResult ReadFile(string filePath) =>
        Read(File.ReadAllText(filePath));

    private static string ExtractYaml(string content, YamlFrontMatterBlock block)
    {
        string slice = content.Substring(block.Span.Start, block.Span.Length);
        List<string> lines = slice.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("---", StringComparison.Ordinal)) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("---", StringComparison.Ordinal)) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static (string Body, int StartLine) ExtractBody(string content, YamlFrontMatterBlock block)
    {
        int afterIndex = block.Span.End + 1;
        while (afterIndex < content.Length && content[afterIndex] is '\r' or '\n')
        {
            afterIndex++;
        }

        int startLine = SourceLineAt(content, afterIndex);
        return afterIndex >= content.Length
            ? (string.Empty, startLine)
            : (content[afterIndex..], startLine);
    }

    private static int SourceLineAt(string content, int position)
    {
        int line = 1;
        for (int index = 0; index < position; index++)
        {
            if (content[index] == '\n' ||
                content[index] == '\r' && (index + 1 >= position || content[index + 1] != '\n'))
            {
                line++;
            }
        }

        return line;
    }
}
