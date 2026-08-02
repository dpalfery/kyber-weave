using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// Sets <c>ontology.docs-root</c> in an existing host configuration, leaving every other
/// byte of the file where it was.
/// </summary>
/// <remarks>
/// <para>
/// A round trip through the deserializer and a YAML emitter would be shorter, but it
/// rewrites the whole document: comments vanish, key order becomes the emitter's, and any
/// key the typed model does not know about is dropped on the floor. A host config is
/// hand-maintained and carries the operator's harness profiles, catalog column overrides
/// and vocabularies, so the edit is done as text — one line changes and the rest of the
/// file is returned verbatim.
/// </para>
/// <para>
/// Line endings are preserved as the dominant style of the input. A file mixing both is
/// normalized to CRLF, which is the one case where this type touches a line it did not
/// mean to; no correctness depends on it, and the alternative is carrying per-line endings
/// through the edit for no reader's benefit.
/// </para>
/// </remarks>
internal static partial class HostConfigYaml
{
    /// <summary>Indentation used for keys this type inserts, matching the emitted template.</summary>
    private const string DefaultIndent = "  ";

    /// <summary>
    /// Returns <paramref name="yaml"/> with <c>ontology.docs-root</c> set to
    /// <paramref name="docsRoot"/>. The key is added, with an <c>ontology:</c> block if
    /// needed, when the file does not already declare it. Returns the input unchanged when
    /// it already says exactly this.
    /// </summary>
    public static string WithDocsRoot(string yaml, string docsRoot)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(docsRoot);

        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var ontology = IndexOfOntologyKey(lines);
        if (ontology < 0)
            return string.Join(newline, AppendOntologyBlock(lines, docsRoot));

        var blockEnd = EndOfBlock(lines, ontology);
        var blockIndent = BlockIndent(lines, ontology, blockEnd);
        for (var i = ontology + 1; i < blockEnd; i++)
        {
            var match = DocsRootKey().Match(lines[i]);
            if (!match.Success || !string.Equals(
                    match.Groups["indent"].Value,
                    blockIndent,
                    StringComparison.Ordinal))
                continue;

            var (value, suffix) = SplitScalarAndSuffix(match.Groups["rest"].Value);
            if (ScalarEquals(value, docsRoot))
                return yaml;

            lines[i] = match.Groups["prefix"].Value + QuoteScalar(docsRoot) + suffix;
            return string.Join(newline, lines);
        }

        lines.Insert(ontology + 1, blockIndent + "docs-root: " + QuoteScalar(docsRoot));
        return string.Join(newline, lines);
    }

    /// <summary>Quotes a single-line string as a YAML scalar without changing its value.</summary>
    internal static string QuoteScalar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
            throw new ArgumentException("YAML scalar values may not contain control characters.", nameof(value));

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    /// <summary>Compares a YAML scalar token by parsed value, preserving its original style.</summary>
    private static bool ScalarEquals(string token, string expected)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader("value: " + token));
            var root = (YamlMappingNode)stream.Documents[0].RootNode;
            return root.Children.TryGetValue(new YamlScalarNode("value"), out var node)
                && node is YamlScalarNode scalar
                && string.Equals(scalar.Value, expected, StringComparison.Ordinal);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Separates a scalar token from trailing whitespace or an inline comment without
    /// treating a hash inside a quoted scalar as the start of that comment.
    /// </summary>
    private static (string Value, string Suffix) SplitScalarAndSuffix(string rest)
    {
        var quote = rest.Length > 0 && rest[0] is '\'' or '"' ? rest[0] : '\0';
        var start = quote == '\0' ? 0 : 1;
        for (var i = start; i < rest.Length; i++)
        {
            if (quote == '\'' && rest[i] == '\'')
            {
                if (i + 1 < rest.Length && rest[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                quote = '\0';
                continue;
            }

            if (quote == '"')
            {
                if (rest[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (rest[i] == '"')
                    quote = '\0';
                continue;
            }

            if (quote != '\0')
                continue;

            if (rest[i] == '#' && i > 0 && char.IsWhiteSpace(rest[i - 1]))
            {
                var suffixStart = i - 1;
                while (suffixStart > 0 && char.IsWhiteSpace(rest[suffixStart - 1]))
                    suffixStart--;

                return (rest[..suffixStart], rest[suffixStart..]);
            }
        }

        var valueEnd = rest.Length;
        while (valueEnd > 0 && char.IsWhiteSpace(rest[valueEnd - 1]))
            valueEnd--;

        return (rest[..valueEnd], rest[valueEnd..]);
    }

    /// <summary>Index of the top-level <c>ontology:</c> key, or -1.</summary>
    private static int IndexOfOntologyKey(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (OntologyKey().IsMatch(lines[i]))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The line at which the block opened by <paramref name="blockStart"/> ends: the next
    /// line starting in column zero with something other than a comment. Blank lines and
    /// full-line comments belong to the block, so a key sitting below one is still found.
    /// </summary>
    private static int EndOfBlock(List<string> lines, int blockStart)
    {
        for (var i = blockStart + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
                continue;

            return i;
        }

        return lines.Count;
    }

    /// <summary>The indentation the block's own keys use, falling back to the template's.</summary>
    private static string BlockIndent(List<string> lines, int blockStart, int blockEnd)
    {
        string? shallowest = null;
        for (var i = blockStart + 1; i < blockEnd; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            var indent = lines[i].Length - trimmed.Length;
            if (indent > 0 && (shallowest is null || indent < shallowest.Length))
                shallowest = lines[i][..indent];
        }

        return shallowest ?? DefaultIndent;
    }

    private static List<string> AppendOntologyBlock(List<string> lines, string docsRoot)
    {
        if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            lines.Add(string.Empty);

        lines.Add("ontology:");
        lines.Add(DefaultIndent + "docs-root: " + QuoteScalar(docsRoot));
        lines.Add(string.Empty);
        return lines;
    }

    [GeneratedRegex(@"^ontology[ \t]*:[ \t]*(#.*)?$")]
    private static partial Regex OntologyKey();

    /// <summary>
    /// Splits a <c>docs-root</c> line into the part before its value and the remaining text,
    /// which is then parsed without confusing a quoted hash for a trailing comment.
    /// </summary>
    [GeneratedRegex(@"^(?<prefix>(?<indent>[ \t]+)docs-root[ \t]*:[ \t]*)(?<rest>.*)$")]
    private static partial Regex DocsRootKey();
}
