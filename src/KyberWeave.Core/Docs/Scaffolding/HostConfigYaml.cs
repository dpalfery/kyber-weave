using System.Text;
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
/// Line endings are the file's own, line by line. A configuration written with lone CR
/// endings is a document YAML reads perfectly well, so it has to be a document this type
/// reads perfectly well too: split on <c>'\n'</c> alone it is one long line, the
/// <c>ontology:</c> key is not where the block starts, and the append that follows writes a
/// second <c>ontology</c> key — a file YAML refuses to load at all. Each line therefore
/// keeps the break that ended it, and only the lines this type adds take a new one, in the
/// style the file uses most.
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

        HostConfigLines lines = HostConfigLines.Read(yaml);

        int ontology = IndexOfOntologyKey(lines);
        if (ontology < 0)
            return AppendOntologyBlock(lines, docsRoot).ToText();

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

            if (TryReadSequence(lines, i, value, blockIndent, out IReadOnlyList<string> roots))
                return WithDocsRootAmong(yaml, roots, docsRoot);

            if (ScalarEquals(value, docsRoot))
                return yaml;

            // A key written with no value at all ends at its colon, so the separating space
            // has to be added back — 'docs-root:' + a scalar is not YAML.
            string prefix = match.Groups["prefix"].Value;
            if (prefix.EndsWith(':')) prefix += " ";

            lines[i] = prefix + QuoteScalar(docsRoot) + suffix;
            return lines.ToText();
        }

        lines.Insert(ontology + 1, blockIndent + "docs-root: " + QuoteScalar(docsRoot));
        return lines.ToText();
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
        HostConfigLines lines,
        int keyIndex,
        string value,
        string blockIndent,
        out IReadOnlyList<string> roots)
    {
        roots = [];

        if (value.StartsWith('['))
        {
            if (!TryCollectFlowSequence(lines, keyIndex, value, out string flow))
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
        HostConfigLines lines,
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

        List<string> parts = [value];
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
    private static int IndexOfOntologyKey(HostConfigLines lines)
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
    private static int EndOfBlock(HostConfigLines lines, int blockStart)
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
    private static string BlockIndent(HostConfigLines lines, int blockStart, int blockEnd)
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

    private static HostConfigLines AppendOntologyBlock(HostConfigLines lines, string docsRoot)
    {
        if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            lines.Add(string.Empty);

        lines.Add("ontology:");
        lines.Add(DefaultIndent + "docs-root: " + QuoteScalar(docsRoot));
        return lines;
    }

    /// <summary>
    /// Returns <paramref name="yaml"/> with <paramref name="technologies"/> merged into
    /// <c>ontology.technologies</c>. If <c>ontology:</c> does not exist, an ontology block
    /// is created. If <c>ontology.technologies</c> does not exist, it is inserted with proper
    /// block indentation. Existing technologies and comments are preserved without duplicates.
    /// </summary>
    /// <remarks>
    /// A scalar <c>technologies: csharp</c> is valid YAML, but inserting <c>- item</c> lines
    /// beneath it leaves a scalar followed by a sequence — the file would still parse as a
    /// scalar and the new items would be orphaned. Only a flow sequence (value starting with
    /// <c>[</c>) is merged in place; a non-empty scalar is rejected so the operator can
    /// replace it with a sequence rather than this command guessing a conversion.
    /// </remarks>
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

        HostConfigLines lines = HostConfigLines.Read(yaml);

        int ontology = IndexOfOntologyKey(lines);
        if (ontology < 0)
        {
            return AppendOntologyWithTechnologies(lines, technologies).ToText();
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

            (string? value, _) = SplitScalarAndSuffix(match.Groups["rest"].Value);

            if (value.StartsWith('['))
            {
                return MergeFlowSequence(yaml, lines, i, value, technologies);
            }

            if (value.Length > 0)
            {
                throw new InvalidDataException(
                    "ontology.technologies is a scalar, not a sequence. Replace the scalar " +
                    "with a YAML sequence (a block list or a [...] flow list) before adding " +
                    "technologies — merging would otherwise leave a scalar followed by " +
                    "orphaned list items.");
            }

            return MergeBlockSequence(yaml, lines, i, blockIndent, blockEnd, technologies);
        }

        return InsertTechnologiesBlock(lines, ontology, blockIndent, blockEnd, technologies);
    }

    private static string MergeFlowSequence(
        string originalYaml,
        HostConfigLines lines,
        int keyIndex,
        string value,
        IReadOnlyList<string> technologies)
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
                lines[closeLineIndex] = prefix + string.Join(", ", toAdd.Select(QuoteScalar)) + afterBracket;
                return lines.ToText();
            }
        }

        string addition = existingTechs.Count > 0
            ? ", " + string.Join(", ", toAdd.Select(QuoteScalar))
            : string.Join(", ", toAdd.Select(QuoteScalar));

        lines[closeLineIndex] = beforeBracket + addition + afterBracket;
        return lines.ToText();
    }

    private static string MergeBlockSequence(
        string originalYaml,
        HostConfigLines lines,
        int keyIndex,
        string blockIndent,
        int blockEnd,
        IReadOnlyList<string> technologies)
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
                int dashIndex = lines[j].IndexOf('-', StringComparison.Ordinal);
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
            newLines.Add(itemIndent + "- " + QuoteScalar(tech));
        }

        lines.InsertRange(lastItemIndex + 1, newLines);
        return lines.ToText();
    }

    private static string InsertTechnologiesBlock(
        HostConfigLines lines,
        int ontologyIndex,
        string blockIndent,
        int blockEnd,
        IReadOnlyList<string> technologies)
    {
        int insertIndex = blockEnd;
        while (insertIndex > ontologyIndex + 1 && string.IsNullOrWhiteSpace(lines[insertIndex - 1]))
        {
            insertIndex--;
        }

        List<string> toInsert =
        [
            blockIndent + "technologies:"
        ];

        string itemIndent = blockIndent + DefaultIndent;
        foreach (string tech in technologies.Distinct(StringComparer.Ordinal))
        {
            toInsert.Add(itemIndent + "- " + QuoteScalar(tech));
        }

        lines.InsertRange(insertIndex, toInsert);
        return lines.ToText();
    }

    private static HostConfigLines AppendOntologyWithTechnologies(
        HostConfigLines lines,
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
            lines.Add(DefaultIndent + DefaultIndent + "- " + QuoteScalar(tech));
        }

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

    /// <summary>
    /// The line ending the file itself uses: whichever of CRLF, LF or a lone CR it holds most
    /// of, with the one that appears first settling a tie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asking whether the file contains a CRLF is not the same question. A file written almost
    /// entirely with LF but holding one CRLF — a pasted line, an edited hunk — would then be
    /// edited in CRLF, and a file written with lone CR would get LF and end up mixed. What this
    /// tool appends is the only part of the file it chooses, so it is the part that has to match
    /// what the operator's editor will keep writing; a majority settles it, and the tie-break is
    /// the first ending the file shows, because that is the one its first line already uses.
    /// </para>
    /// <para>
    /// <see cref="McpDeclarationBlockWriter"/> appends its section to the same file and answers
    /// the same question through this method rather than a copy, so a configuration cannot end
    /// up carrying two appended sections written in two different styles.
    /// </para>
    /// </remarks>
    internal static string DetectNewline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int crlf = 0;
        int lf = 0;
        int cr = 0;
        int firstCrlf = -1;
        int firstLf = -1;
        int firstCr = -1;

        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];

            if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                crlf++;
                firstCrlf = firstCrlf < 0 ? index : firstCrlf;
                index++;
                continue;
            }

            if (current == '\n')
            {
                lf++;
                firstLf = firstLf < 0 ? index : firstLf;
                continue;
            }

            if (current == '\r')
            {
                cr++;
                firstCr = firstCr < 0 ? index : firstCr;
            }
        }

        // A file with no line break at all gets LF, the style this repository writes.
        int most = Math.Max(crlf, Math.Max(lf, cr));
        if (most == 0)
            return "\n";

        int earliest = int.MaxValue;
        string newline = "\n";

        if (crlf == most && firstCrlf < earliest)
        {
            earliest = firstCrlf;
            newline = "\r\n";
        }

        if (lf == most && firstLf < earliest)
        {
            earliest = firstLf;
            newline = "\n";
        }

        if (cr == most && firstCr < earliest)
            newline = "\r";

        return newline;
    }

    /// <summary>The top-level <c>ontology:</c> key.</summary>
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

    /// <summary>
    /// A host configuration read as lines, each carrying the line break that ended it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split on <c>'\n'</c> alone, a document written with lone CR endings is one long line, and
    /// every key this type looks for in column zero is looked for where the file does not have
    /// it: the <c>ontology:</c> block goes unfound, a second one is appended, and YAML refuses to
    /// load the result at all. A lone CR is a line break to YAML, so it has to be one here too.
    /// </para>
    /// <para>
    /// Each line keeps its own terminator rather than one style for the document, because this
    /// type edits a line and has no business normalizing the rest of an operator's file — the
    /// diff would show changes nobody made, on a file that escalates for human review by path
    /// alone. Only the lines this tool adds take a new terminator, and they take the document's
    /// dominant style.
    /// </para>
    /// </remarks>
    private sealed class HostConfigLines
    {
        private readonly List<string> _content = [];
        private readonly List<string> _terminators = [];
        private readonly string _newline;

        private HostConfigLines(string newline) => _newline = newline;

        public int Count => _content.Count;

        public string this[int index]
        {
            get => _content[index];
            set => _content[index] = value;
        }

        public static HostConfigLines Read(string text)
        {
            HostConfigLines lines = new(DetectNewline(text));

            int start = 0;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    lines.Append(text[start..index], "\r\n");
                    index++;
                    start = index + 1;
                    continue;
                }

                if (text[index] is '\r' or '\n')
                {
                    lines.Append(text[start..index], text[index].ToString());
                    start = index + 1;
                }
            }

            if (start < text.Length)
                lines.Append(text[start..], string.Empty);

            return lines;
        }

        public void Add(string line)
        {
            Terminate(_content.Count - 1);
            Append(line, _newline);
        }

        public void Insert(int index, string line)
        {
            Terminate(index - 1);
            AppendAt(index, line, _newline);
        }

        public void InsertRange(int index, IEnumerable<string> items)
        {
            Terminate(index - 1);
            foreach (string item in items)
            {
                AppendAt(index, item, _newline);
                index++;
            }
        }

        public string ToText()
        {
            StringBuilder text = new();
            for (int index = 0; index < _content.Count; index++)
            {
                text.Append(_content[index]).Append(_terminators[index]);
            }

            return text.ToString();
        }

        private void Append(string content, string terminator)
        {
            _content.Add(content);
            _terminators.Add(terminator);
        }

        private void AppendAt(int index, string content, string terminator)
        {
            _content.Insert(index, content);
            _terminators.Insert(index, terminator);
        }

        /// <summary>
        /// Gives the line at <paramref name="index"/> the document's line break when it has none.
        /// A line added below one that ended the file — no break after it — would otherwise join
        /// that line rather than follow it.
        /// </summary>
        private void Terminate(int index)
        {
            if (index >= 0 && index < _terminators.Count && _terminators[index].Length == 0)
                _terminators[index] = _newline;
        }
    }
}
