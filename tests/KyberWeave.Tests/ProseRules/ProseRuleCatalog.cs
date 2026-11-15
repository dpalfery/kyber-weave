using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using KyberWeave.Core.Review;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests.ProseRules;

/// <summary>A document a rule names: a repository-relative path or glob, and optionally the heading of its section.</summary>
/// <param name="Pattern">A repository-relative path, or a glob with <c>*</c> and <c>**</c>.</param>
/// <param name="Section">The heading text of the section that carries the rule, without the leading <c>#</c>s.</param>
/// <param name="Anchor">
/// For a home the grader cannot judge reliably: a stable marker the document must carry, checked offline
/// instead of asking the grader. Never a sentence of the rule.
/// </param>
internal sealed record ProseDocumentRef(string Pattern, string? Section, string? Anchor = null);

/// <summary>A negative control: a real document the rule must be judged absent from.</summary>
/// <param name="Path">A repository-relative path.</param>
/// <param name="WithoutSection">When set, that section is cut out first, so the control is the home document minus its rule.</param>
internal sealed record ProseControl(string Path, string? WithoutSection);

/// <summary>The retired opposite of a rule, which the listed documents must not state.</summary>
/// <param name="Statement">The retired rule in one sentence.</param>
/// <param name="Example">A sentence that plainly states it, inserted into a real document as the positive control.</param>
/// <param name="AbsentFrom">The documents that must not state it.</param>
/// <param name="SeedInto">The document the example is inserted into; the first <paramref name="AbsentFrom"/> file when unset.</param>
internal sealed record ProseRetiredRule(string Statement, string Example, IReadOnlyList<ProseDocumentRef> AbsentFrom, string? SeedInto = null);

/// <summary>One rule the shipped prose must state.</summary>
/// <param name="Id">Stable id; the worker rule map's ids (A1 to G7) where the rule is one of those.</param>
/// <param name="Statement">The rule in one sentence: the one judgment the grader is asked about.</param>
/// <param name="StatedIn">The documents that must state it.</param>
/// <param name="Controls">Documents the grader must judge as not stating it.</param>
/// <param name="Retired">The retired opposite, when one must not come back.</param>
internal sealed record ProseRule(
    string Id,
    string Statement,
    IReadOnlyList<ProseDocumentRef> StatedIn,
    IReadOnlyList<ProseControl> Controls,
    ProseRetiredRule? Retired);

/// <summary>
/// The rule catalog: the one list of rules the shipped prose must state, which the offline
/// structural tests and the model-graded test both read.
/// </summary>
/// <param name="ConfidenceFloor">The confidence the expected answer must reach to count.</param>
/// <param name="Rules">The rules, in file order.</param>
internal sealed partial record ProseRuleCatalog(double ConfidenceFloor, IReadOnlyList<ProseRule> Rules)
{
    private static readonly string[] TopKeys = ["version", "confidence-floor", "rules"];
    private static readonly string[] RuleKeys = ["id", "statement", "stated-in", "controls", "retired"];
    private static readonly string[] DocumentKeys = ["path", "section", "anchor"];
    private static readonly string[] ControlKeys = ["path", "without-section"];
    private static readonly string[] RetiredKeys = ["statement", "example", "absent-from", "seed-into"];

    /// <summary>The catalog shipped with the tests.</summary>
    public static string DefaultPath([CallerFilePath] string sourcePath = "") =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "Fixtures", "prose-rules", "catalog.yml");

    /// <summary>Loads and parses the catalog; an unknown key, a missing field or a wrong type is refused by name.</summary>
    public static ProseRuleCatalog Load(string path)
    {
        YamlStream stream = new();
        using (StreamReader reader = File.OpenText(path))
        {
            stream.Load(reader);
        }

        YamlMappingNode root = stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlMappingNode mapping
            ? mapping
            : throw new InvalidDataException($"{path}: expected one YAML document holding a mapping.");
        RefuseUnknownKeys(root, TopKeys, "catalog");

        string version = Scalar(root, "version", "catalog");
        if (version != "1")
        {
            throw new InvalidDataException($"catalog: version '{version}' is not supported; expected 1.");
        }

        double floor = double.Parse(Scalar(root, "confidence-floor", "catalog"), CultureInfo.InvariantCulture);
        if (floor is <= 0.5 or > 1)
        {
            throw new InvalidDataException($"catalog: confidence-floor {floor} must be above 0.5 and at most 1.");
        }

        List<ProseRule> rules = [];
        foreach (YamlNode node in Sequence(root, "rules", "catalog"))
        {
            rules.Add(ParseRule(node as YamlMappingNode ?? throw new InvalidDataException("catalog: every rule must be a mapping.")));
        }

        return new ProseRuleCatalog(floor, rules);
    }

    /// <summary>Expands a document reference to the existing repository-relative files it names, sorted.</summary>
    public static IReadOnlyList<string> Resolve(string repositoryRoot, ProseDocumentRef document)
    {
        if (!document.Pattern.Contains('*', StringComparison.Ordinal))
        {
            return File.Exists(Path.Combine(repositoryRoot, document.Pattern)) ? [document.Pattern] : [];
        }

        int wildcard = document.Pattern.IndexOf('*', StringComparison.Ordinal);
        int lastSlash = document.Pattern.LastIndexOf('/', wildcard);
        string prefix = lastSlash < 0 ? string.Empty : document.Pattern[..lastSlash];
        string start = Path.Combine(repositoryRoot, prefix);
        if (!Directory.Exists(start))
        {
            return [];
        }

        return Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'))
            .Where(relative => PathGlob.IsMatch(document.Pattern, relative))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The section under the heading whose text is <paramref name="heading"/>: that heading line up to the next
    /// heading of the same or a higher level. Null when no heading matches. Headings inside fenced code are ignored.
    /// </summary>
    public static string? Section(string text, string heading)
    {
        (int Start, int End)? range = SectionRange(text, heading);
        return range is { } found ? text[found.Start..found.End] : null;
    }

    /// <summary>The text with the section under <paramref name="heading"/> removed; null when no heading matches.</summary>
    public static string? WithoutSection(string text, string heading)
    {
        (int Start, int End)? range = SectionRange(text, heading);
        return range is { } found ? string.Concat(text.AsSpan(0, found.Start), text.AsSpan(found.End)) : null;
    }

    /// <summary>
    /// Splits a document at its level-1 and level-2 headings, then joins neighbouring pieces while they stay
    /// under <paramref name="targetChars"/>, so each part is one or a few whole sections.
    /// </summary>
    public static IReadOnlyList<string> Parts(string text, int targetChars)
    {
        List<int> starts = [0, .. HeadingLines(text).Where(heading => heading.Level <= 2 && heading.Offset > 0).Select(heading => heading.Offset)];
        List<string> pieces = [];
        for (int index = 0; index < starts.Count; index++)
        {
            int end = index + 1 < starts.Count ? starts[index + 1] : text.Length;
            pieces.Add(text[starts[index]..end]);
        }

        List<string> parts = [];
        string current = string.Empty;
        foreach (string piece in pieces)
        {
            if (current.Length > 0 && current.Length + piece.Length > targetChars)
            {
                parts.Add(current);
                current = string.Empty;
            }

            current += piece;
        }

        if (current.Trim().Length > 0)
        {
            parts.Add(current);
        }

        return parts;
    }

    /// <summary>Every Markdown heading text in the document, outside fenced code.</summary>
    public static IReadOnlyList<string> Headings(string text) =>
        HeadingLines(text).Select(heading => heading.Text).ToList();

    private static (int Start, int End)? SectionRange(string text, string heading)
    {
        List<(int Offset, int Level, string Text)> headings = HeadingLines(text);
        int index = headings.FindIndex(item => string.Equals(item.Text, heading.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        (int offset, int level, _) = headings[index];
        int end = text.Length;
        for (int next = index + 1; next < headings.Count; next++)
        {
            if (headings[next].Level <= level)
            {
                end = headings[next].Offset;
                break;
            }
        }

        return (offset, end);
    }

    private static List<(int Offset, int Level, string Text)> HeadingLines(string text)
    {
        List<(int, int, string)> headings = [];
        bool fenced = false;
        int offset = 0;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced && HeadingPattern().Match(trimmed) is { Success: true } match)
            {
                headings.Add((offset, match.Groups[1].Length, match.Groups[2].Value.Trim()));
            }

            offset += line.Length + 1;
        }

        return headings;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    private static ProseRule ParseRule(YamlMappingNode node)
    {
        string id = Scalar(node, "id", "rule");
        string where = $"rule {id}";
        RefuseUnknownKeys(node, RuleKeys, where);

        List<ProseDocumentRef> statedIn = [.. Sequence(node, "stated-in", where).Select(item => ParseDocument(item, where))];
        List<ProseControl> controls = node.Children.ContainsKey(new YamlScalarNode("controls"))
            ? [.. Sequence(node, "controls", where).Select(item => ParseControl(item, where))]
            : [];
        ProseRetiredRule? retired = node.Children.TryGetValue(new YamlScalarNode("retired"), out YamlNode? retiredNode)
            ? ParseRetired(retiredNode as YamlMappingNode ?? throw new InvalidDataException($"{where}: retired must be a mapping."), where)
            : null;
        return new ProseRule(id, Scalar(node, "statement", where), statedIn, controls, retired);
    }

    private static ProseDocumentRef ParseDocument(YamlNode node, string where)
    {
        if (node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            return new ProseDocumentRef(scalar.Value, null);
        }

        YamlMappingNode mapping = node as YamlMappingNode
            ?? throw new InvalidDataException($"{where}: a document is a path or a mapping with path and section.");
        RefuseUnknownKeys(mapping, DocumentKeys, where);
        return new ProseDocumentRef(
            Scalar(mapping, "path", where), OptionalScalar(mapping, "section", where), OptionalScalar(mapping, "anchor", where));
    }

    private static ProseControl ParseControl(YamlNode node, string where)
    {
        if (node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            return new ProseControl(scalar.Value, null);
        }

        YamlMappingNode mapping = node as YamlMappingNode
            ?? throw new InvalidDataException($"{where}: a control is a path or a mapping with path and without-section.");
        RefuseUnknownKeys(mapping, ControlKeys, where);
        return new ProseControl(Scalar(mapping, "path", where), OptionalScalar(mapping, "without-section", where));
    }

    private static ProseRetiredRule ParseRetired(YamlMappingNode node, string where)
    {
        RefuseUnknownKeys(node, RetiredKeys, where + " retired");
        return new ProseRetiredRule(
            Scalar(node, "statement", where + " retired"),
            Scalar(node, "example", where + " retired"),
            [.. Sequence(node, "absent-from", where + " retired").Select(item => ParseDocument(item, where))],
            OptionalScalar(node, "seed-into", where + " retired"));
    }

    private static void RefuseUnknownKeys(YamlMappingNode node, string[] allowed, string where)
    {
        foreach (YamlNode key in node.Children.Keys)
        {
            string name = (key as YamlScalarNode)?.Value ?? string.Empty;
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"{where}: unknown key '{name}'; allowed: {string.Join(", ", allowed)}.");
            }
        }
    }

    private static string Scalar(YamlMappingNode node, string key, string where) =>
        OptionalScalar(node, key, where) ?? throw new InvalidDataException($"{where}: '{key}' is required.");

    private static string? OptionalScalar(YamlMappingNode node, string key, string where)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            return null;
        }

        return value is YamlScalarNode { Value: { } text } && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : throw new InvalidDataException($"{where}: '{key}' must be a non-empty string.");
    }

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key, string where) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) && value is YamlSequenceNode { Children.Count: > 0 } sequence
            ? sequence
            : throw new InvalidDataException($"{where}: '{key}' must be a non-empty list.");
}
