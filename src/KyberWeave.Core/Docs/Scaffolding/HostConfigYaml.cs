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

        string newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = yaml.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        int ontology = IndexOfOntologyKey(lines);
        if (ontology < 0)
            return string.Join(newline, AppendOntologyBlock(lines, docsRoot));

        int blockEnd = EndOfBlock(lines, ontology);
        string blockIndent = BlockIndent(lines, ontology, blockEnd);
        for (int i = ontology + 1; i < blockEnd; i++)
        {
            Match match = DocsRootKey().Match(lines[i]);
            if (!match.Success || !string.Equals(
                    match.Groups["indent"].Value,
                    blockIndent,
                    StringComparison.Ordinal))
                continue;

            (string? value, string? suffix) = SplitScalarAndSuffix(match.Groups["rest"].Value);

            if (TryReadSequence(lines, i, value, blockIndent, out IReadOnlyList<string>? roots))
                return WithDocsRootAmong(yaml, roots, docsRoot);

            if (ScalarEquals(value, docsRoot))
                return yaml;

            // A key written with no value at all ends at its colon, so the separating space
            // has to be added back — 'docs-root:' + a scalar is not YAML.
            string prefix = match.Groups["prefix"].Value;
            if (prefix.EndsWith(':')) prefix += " ";

            lines[i] = prefix + QuoteScalar(docsRoot) + suffix;
            return string.Join(newline, lines);
        }

        lines.Insert(ontology + 1, blockIndent + "docs-root: " + QuoteScalar(docsRoot));
        return string.Join(newline, lines);
    }

    /// <summary>
    /// Resolves a multi-root <c>docs-root</c> against the root being scaffolded into. A
    /// listed root leaves the file alone; anything else stops the scaffold.
    /// </summary>
    /// <remarks>
    /// The single-root path rewrites the key's value, which against a list would leave the
    /// <c>- item</c> lines orphaned below a scalar — the config would still parse, and would
    /// mean something the operator never wrote. Ordering a host's roots is a decision this
    /// command does not get to make on their behalf, so the unlisted case reports rather
    /// than guesses. Everything else <c>docs init</c> writes is unaffected: only the config
    /// is operator state.
    /// </remarks>
    private static string WithDocsRootAmong(string yaml, IReadOnlyList<string> roots, string docsRoot)
    {
        if (roots.Contains(docsRoot, StringComparer.Ordinal))
            return yaml;

        throw new InvalidDataException(
            $"ontology.docs-root lists {roots.Count} documentation roots and none of them is " +
            $"'{docsRoot}': {string.Join(", ", roots)}. Add it to that list by hand — the " +
            "order of the roots is yours, and the first is the one this command scaffolds into.");
    }

    /// <summary>
    /// Reads the entries of a <c>docs-root</c> written as a sequence, in either block or
    /// flow style. False when the key carries a scalar, which the caller rewrites in place.
    /// </summary>
    /// <remarks>
    /// A value that opens with <c>[</c> is always a flow sequence, never a scalar. Failing
    /// to collect it — including a multi-line form — must not fall through to the scalar
    /// rewrite, which would orphan the continuation lines below a new scalar value.
    /// </remarks>
    private static bool TryReadSequence(
        List<string> lines,
        int keyIndex,
        string value,
        string blockIndent,
        out IReadOnlyList<string> roots)
    {
        roots = [];

        if (value.StartsWith('['))
        {
            if (!TryCollectFlowSequence(lines, keyIndex, value, out string? flow))
            {
                throw new InvalidDataException(
                    "ontology.docs-root looks like a flow sequence but its brackets do not " +
                    "balance. Fix the list by hand — rewriting the key would leave orphaned " +
                    "continuation lines below a scalar.");
            }

            if (!TryParseSequence("value: " + flow, out roots))
            {
                throw new InvalidDataException(
                    "ontology.docs-root looks like a flow sequence but could not be read. " +
                    "Fix the list by hand — rewriting the key would leave orphaned " +
                    "continuation lines below a scalar.");
            }

            return true;
        }

        if (value.Length > 0)
            return false;

        // A block sequence lives on the lines below the key. Its items may be indented past
        // the key or sit at the key's own indent — both are valid YAML — so an item is
        // recognised by its '-', and the first line that is not one ends the sequence.
        List<string> items = new List<string>();
        for (int i = keyIndex + 1; i < lines.Count; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            int indent = lines[i].Length - trimmed.Length;
            if (trimmed[0] != '-' || indent < blockIndent.Length) break;

            items.Add(lines[i]);
        }

        return items.Count > 0 && TryParseSequence("value:\n" + string.Join('\n', items), out roots);
    }

    /// <summary>
    /// Collects a flow sequence that may span several lines, stopping when brackets balance.
    /// </summary>
    private static bool TryCollectFlowSequence(
        List<string> lines,
        int keyIndex,
        string value,
        out string flow)
    {
        int depth = FlowBracketDepth(value);
        if (depth == 0)
        {
            flow = value;
            return true;
        }

        if (depth < 0)
        {
            flow = string.Empty;
            return false;
        }

        List<string> parts = new List<string> { value };
        for (int i = keyIndex + 1; i < lines.Count && depth > 0; i++)
        {
            parts.Add(lines[i]);
            depth += FlowBracketDepth(lines[i]);
            if (depth < 0)
            {
                flow = string.Empty;
                return false;
            }
        }

        if (depth != 0)
        {
            flow = string.Empty;
            return false;
        }

        flow = string.Join('\n', parts);
        return true;
    }

    /// <summary>
    /// Net change in <c>[]</c> nesting across <paramref name="text"/>, ignoring brackets
    /// inside quoted scalars so a path like <c>"a[b]"</c> does not skew the depth.
    /// </summary>
    private static int FlowBracketDepth(string text)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote == '\'')
            {
                if (c == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    quote = '\0';
                }

                continue;
            }

            if (quote == '"')
            {
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == '"') quote = '\0';
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (c == '[') depth++;
            else if (c == ']') depth--;
        }

        return depth;
    }

    private static bool TryParseSequence(string document, out IReadOnlyList<string> roots)
    {
        roots = [];
        try
        {
            YamlStream stream = new YamlStream();
            using StringReader reader = new StringReader(document);
            stream.Load(reader);
            YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;
            if (!root.Children.TryGetValue(new YamlScalarNode("value"), out YamlNode? node) ||
                node is not YamlSequenceNode sequence)
            {
                return false;
            }

            List<string> values = new List<string>();
            foreach (YamlNode child in sequence.Children)
            {
                if (child is not YamlScalarNode { Value: { } scalar }) return false;
                values.Add(scalar);
            }

            roots = values;
            return values.Count > 0;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }
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
            YamlStream stream = new YamlStream();
            using StringReader reader = new StringReader("value: " + token);
            stream.Load(reader);
            YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;
            return root.Children.TryGetValue(new YamlScalarNode("value"), out YamlNode? node)
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
        char quote = rest.Length > 0 && rest[0] is '\'' or '"' ? rest[0] : '\0';
        int start = quote == '\0' ? 0 : 1;
        for (int i = start; i < rest.Length; i++)
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
                int suffixStart = i - 1;
                while (suffixStart > 0 && char.IsWhiteSpace(rest[suffixStart - 1]))
                    suffixStart--;

                return (rest[..suffixStart], rest[suffixStart..]);
            }
        }

        int valueEnd = rest.Length;
        while (valueEnd > 0 && char.IsWhiteSpace(rest[valueEnd - 1]))
            valueEnd--;

        return (rest[..valueEnd], rest[valueEnd..]);
    }

    /// <summary>Index of the top-level <c>ontology:</c> key, or -1.</summary>
    private static int IndexOfOntologyKey(List<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
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
        for (int i = blockStart + 1; i < lines.Count; i++)
        {
            string line = lines[i];
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
        for (int i = blockStart + 1; i < blockEnd; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            int indent = lines[i].Length - trimmed.Length;
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

    /// <summary>
    /// Returns <paramref name="yaml"/> with <paramref name="technologies"/> merged into
    /// <c>ontology.technologies</c>. If <c>ontology:</c> does not exist, an ontology block
    /// is created. If <c>ontology.technologies</c> does not exist, it is inserted with proper
    /// block indentation. Existing technologies and comments are preserved without duplicates.
    /// </summary>
    public static string WithTechnologies(string yaml, IReadOnlyList<string> technologies)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(technologies);

        if (technologies.Count == 0)
        {
            return yaml;
        }

        foreach (string tech in technologies)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tech);
            if (tech.Any(char.IsControl))
            {
                throw new ArgumentException("Technology names may not contain control characters.", nameof(technologies));
            }
        }

        string newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = yaml.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        int ontology = IndexOfOntologyKey(lines);
        if (ontology < 0)
        {
            return string.Join(newline, AppendOntologyWithTechnologies(lines, technologies));
        }

        int blockEnd = EndOfBlock(lines, ontology);
        string blockIndent = BlockIndent(lines, ontology, blockEnd);

        for (int i = ontology + 1; i < blockEnd; i++)
        {
            Match match = TechnologiesKey().Match(lines[i]);
            if (!match.Success || !string.Equals(
                    match.Groups["indent"].Value,
                    blockIndent,
                    StringComparison.Ordinal))
            {
                continue;
            }

            (string? value, string? suffix) = SplitScalarAndSuffix(match.Groups["rest"].Value);

            if (value.StartsWith('['))
            {
                return MergeFlowSequence(yaml, lines, i, value, technologies, newline);
            }

            return MergeBlockSequence(yaml, lines, i, blockIndent, blockEnd, technologies, newline);
        }

        return InsertTechnologiesBlock(lines, ontology, blockIndent, blockEnd, technologies, newline);
    }

    private static string MergeFlowSequence(
        string originalYaml,
        List<string> lines,
        int keyIndex,
        string value,
        IReadOnlyList<string> technologies,
        string newline)
    {
        if (!TryCollectFlowSequence(lines, keyIndex, value, out string flow))
        {
            throw new InvalidDataException(
                "ontology.technologies looks like a flow sequence but its brackets do not " +
                "balance. Fix the list by hand.");
        }

        if (!TryParseSequenceAllowEmpty("value: " + flow, out IReadOnlyList<string> existingTechs))
        {
            throw new InvalidDataException(
                "ontology.technologies looks like a flow sequence but could not be read. " +
                "Fix the list by hand.");
        }

        List<string> toAdd = technologies
            .Where(t => !existingTechs.Contains(t, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (toAdd.Count == 0)
        {
            return originalYaml;
        }

        int depth = 0;
        int closeLineIndex = keyIndex;
        int closeBracketCol = -1;

        for (int i = keyIndex; i < lines.Count; i++)
        {
            string lineText = i == keyIndex ? value : lines[i];
            int prevDepth = depth;
            depth += FlowBracketDepth(lineText);
            if ((depth == 0 && prevDepth != 0) || (i == keyIndex && depth == 0))
            {
                closeLineIndex = i;
                closeBracketCol = lines[i].LastIndexOf(']');
                break;
            }
        }

        if (closeBracketCol < 0)
        {
            closeLineIndex = keyIndex;
            closeBracketCol = lines[keyIndex].LastIndexOf(']');
        }

        string targetLine = lines[closeLineIndex];
        string beforeBracket = targetLine[..closeBracketCol];
        string afterBracket = targetLine[closeBracketCol..];

        int openBracketIndex = beforeBracket.LastIndexOf('[');
        if (existingTechs.Count == 0 && openBracketIndex >= 0)
        {
            string prefix = beforeBracket[..(openBracketIndex + 1)];
            string middle = beforeBracket[(openBracketIndex + 1)..];
            if (string.IsNullOrWhiteSpace(middle))
            {
                lines[closeLineIndex] = prefix + string.Join(", ", toAdd) + afterBracket;
                return string.Join(newline, lines);
            }
        }

        string addition = existingTechs.Count > 0
            ? ", " + string.Join(", ", toAdd)
            : string.Join(", ", toAdd);

        lines[closeLineIndex] = beforeBracket + addition + afterBracket;
        return string.Join(newline, lines);
    }

    private static string MergeBlockSequence(
        string originalYaml,
        List<string> lines,
        int keyIndex,
        string blockIndent,
        int blockEnd,
        IReadOnlyList<string> technologies,
        string newline)
    {
        List<string> existingTechs = new List<string>();
        int lastItemIndex = keyIndex;
        string itemIndent = blockIndent + DefaultIndent;

        for (int j = keyIndex + 1; j < blockEnd; j++)
        {
            string trimmed = lines[j].TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed[0] == '#')
            {
                continue;
            }

            int lineIndent = lines[j].Length - trimmed.Length;
            if (lineIndent < blockIndent.Length)
            {
                break;
            }

            if (trimmed[0] == '-')
            {
                lastItemIndex = j;
                int dashIndex = lines[j].IndexOf('-');
                itemIndent = lines[j][..dashIndex];

                string afterDash = trimmed[1..].TrimStart();
                (string itemVal, _) = SplitScalarAndSuffix(afterDash);
                itemVal = itemVal.Trim('\'', '"');
                if (!string.IsNullOrEmpty(itemVal))
                {
                    existingTechs.Add(itemVal);
                }
            }
            else if (lineIndent <= blockIndent.Length)
            {
                break;
            }
        }

        List<string> toAdd = technologies
            .Where(t => !existingTechs.Contains(t, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (toAdd.Count == 0)
        {
            return originalYaml;
        }

        List<string> newLines = new List<string>();
        foreach (string tech in toAdd)
        {
            newLines.Add(itemIndent + "- " + tech);
        }

        lines.InsertRange(lastItemIndex + 1, newLines);
        return string.Join(newline, lines);
    }

    private static string InsertTechnologiesBlock(
        List<string> lines,
        int ontologyIndex,
        string blockIndent,
        int blockEnd,
        IReadOnlyList<string> technologies,
        string newline)
    {
        int insertIndex = blockEnd;
        while (insertIndex > ontologyIndex + 1 && string.IsNullOrWhiteSpace(lines[insertIndex - 1]))
        {
            insertIndex--;
        }

        List<string> toInsert = new List<string>
        {
            blockIndent + "technologies:"
        };

        string itemIndent = blockIndent + DefaultIndent;
        foreach (string tech in technologies.Distinct(StringComparer.Ordinal))
        {
            toInsert.Add(itemIndent + "- " + tech);
        }

        lines.InsertRange(insertIndex, toInsert);
        return string.Join(newline, lines);
    }

    private static List<string> AppendOntologyWithTechnologies(
        List<string> lines,
        IReadOnlyList<string> technologies)
    {
        if (lines.Count > 0 && lines[^1].Trim().Length > 0)
        {
            lines.Add(string.Empty);
        }

        lines.Add("ontology:");
        lines.Add(DefaultIndent + "technologies:");
        foreach (string tech in technologies.Distinct(StringComparer.Ordinal))
        {
            lines.Add(DefaultIndent + DefaultIndent + "- " + tech);
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static bool TryParseSequenceAllowEmpty(string document, out IReadOnlyList<string> items)
    {
        items = [];
        try
        {
            YamlStream stream = new YamlStream();
            using StringReader reader = new StringReader(document);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
            {
                return false;
            }

            YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;
            if (!root.Children.TryGetValue(new YamlScalarNode("value"), out YamlNode? node) ||
                node is not YamlSequenceNode sequence)
            {
                return false;
            }

            List<string> values = new List<string>();
            foreach (YamlNode child in sequence.Children)
            {
                if (child is not YamlScalarNode { Value: { } scalar })
                {
                    return false;
                }

                values.Add(scalar);
            }

            items = values;
            return true;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^ontology[ \t]*:[ \t]*(#.*)?$")]
    private static partial Regex OntologyKey();

    /// <summary>
    /// Splits a <c>docs-root</c> line into the part before its value and the remaining text,
    /// which is then parsed without confusing a quoted hash for a trailing comment.
    /// </summary>
    [GeneratedRegex(@"^(?<prefix>(?<indent>[ \t]+)docs-root[ \t]*:[ \t]*)(?<rest>.*)$")]
    private static partial Regex DocsRootKey();

    /// <summary>
    /// Splits a <c>technologies</c> line into the part before its value and the remaining text.
    /// </summary>
    [GeneratedRegex(@"^(?<prefix>(?<indent>[ \t]+)technologies[ \t]*:[ \t]*)(?<rest>.*)$")]
    private static partial Regex TechnologiesKey();
}
