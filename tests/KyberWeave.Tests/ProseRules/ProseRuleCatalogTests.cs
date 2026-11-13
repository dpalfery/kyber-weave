using System.Text.RegularExpressions;
using KyberWeave.Tests.ProseRules;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The offline half of the prose checks: the rule catalog is well formed and every document, section and
/// anchor it names exists, so deleting a file, a heading or a catalog entry fails here without a model call.
/// Whether each document actually states its rules is <see cref="ProseRuleGradingTests"/>'s question.
/// </summary>
public sealed partial class ProseRuleCatalogTests
{
    private static readonly string Root = KyberWeaveTestPaths.ToolRoot;

    private static readonly Lazy<ProseRuleCatalog> Catalog = new(() => ProseRuleCatalog.Load(ProseRuleCatalog.DefaultPath()));

    [Fact]
    public void EveryRuleHasAUniqueIdAStatementAHomeAndANegativeControl()
    {
        IReadOnlyList<ProseRule> rules = Catalog.Value.Rules;

        Assert.NotEmpty(rules);
        Assert.Empty(rules.GroupBy(rule => rule.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key));
        Assert.All(rules, rule =>
        {
            Assert.True(IdPattern().IsMatch(rule.Id), $"{rule.Id}: an id is a worker-rule-map id or lower-case words joined by hyphens.");
            Assert.NotEmpty(rule.StatedIn);
            Assert.NotEmpty(rule.Controls);
        });
    }

    [Fact]
    public void EveryDocumentTheCatalogNamesExists()
    {
        List<string> missing = [];
        foreach (ProseRule rule in Catalog.Value.Rules)
        {
            IEnumerable<ProseDocumentRef> documents = rule.StatedIn.Concat(rule.Retired?.AbsentFrom ?? []);
            missing.AddRange(documents
                .Where(document => ProseRuleCatalog.Resolve(Root, document).Count == 0)
                .Select(document => $"{rule.Id}: {document.Pattern} matches no file"));
            missing.AddRange(rule.Controls
                .Where(control => !File.Exists(Path.Combine(Root, control.Path)))
                .Select(control => $"{rule.Id}: control {control.Path} does not exist"));
            if (rule.Retired?.SeedInto is { } seed && !File.Exists(Path.Combine(Root, seed)))
            {
                missing.Add($"{rule.Id}: seed-into {seed} does not exist");
            }
        }

        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    [Fact]
    public void EverySectionTheCatalogNamesIsAHeadingOfItsDocument()
    {
        List<string> missing = [];
        foreach (ProseRule rule in Catalog.Value.Rules)
        {
            foreach (ProseDocumentRef document in rule.StatedIn.Concat(rule.Retired?.AbsentFrom ?? []).Where(item => item.Section is not null))
            {
                missing.AddRange(ProseRuleCatalog.Resolve(Root, document)
                    .Where(path => ProseRuleCatalog.Section(Read(path), document.Section!) is null)
                    .Select(path => $"{rule.Id}: {path} has no heading '{document.Section}'"));
            }

            missing.AddRange(rule.Controls
                .Where(control => control.WithoutSection is not null
                    && File.Exists(Path.Combine(Root, control.Path))
                    && ProseRuleCatalog.Section(Read(control.Path), control.WithoutSection) is null)
                .Select(control => $"{rule.Id}: control {control.Path} has no heading '{control.WithoutSection}'"));
        }

        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    [Fact]
    public void EveryAnchoredHomeCarriesItsAnchor()
    {
        List<string> missing = [];
        foreach (ProseRule rule in Catalog.Value.Rules)
        {
            foreach (ProseDocumentRef document in rule.StatedIn.Where(item => item.Anchor is not null))
            {
                missing.AddRange(ProseRuleCatalog.Resolve(Root, document)
                    .Where(path => !Read(path).Contains(document.Anchor!, StringComparison.Ordinal))
                    .Select(path => $"{rule.Id}: {path} lacks its anchor '{document.Anchor}'"));
            }
        }

        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    [Fact]
    public void EveryControlThatCutsASectionLeavesTextBehind()
    {
        foreach (ProseRule rule in Catalog.Value.Rules)
        {
            foreach (ProseControl control in rule.Controls.Where(item => item.WithoutSection is not null))
            {
                string? cut = ProseRuleCatalog.WithoutSection(Read(control.Path), control.WithoutSection!);
                Assert.True(cut is { Length: > 200 }, $"{rule.Id}: cutting '{control.WithoutSection}' from {control.Path} leaves no document to judge.");
            }
        }
    }

    [Fact]
    public void EveryRetiredRuleHasARealDocumentToSeedItsExampleInto()
    {
        Assert.All(Catalog.Value.Rules.Where(rule => rule.Retired is not null), rule =>
        {
            Assert.NotEmpty(rule.Retired!.AbsentFrom);
            Assert.False(string.Equals(rule.Retired.Statement, rule.Statement, StringComparison.Ordinal), $"{rule.Id}: the retired rule repeats the rule.");
        });
    }

    /// <summary>
    /// The worker rule map and the catalog are one list: every map row the map says the catalog enforces
    /// is a catalog rule, and every catalog rule with a map id is a row of the map.
    /// </summary>
    [Fact]
    public void TheWorkerRuleMapAndTheCatalogNameTheSameRules()
    {
        string map = Read("docs/kyber-squad/worker-rule-map.md");
        Dictionary<string, string> rows = MapRow().Matches(map)
            .ToDictionary(match => match.Groups["id"].Value, match => match.Value, StringComparer.Ordinal);
        HashSet<string> catalogued = Catalog.Value.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(rows);
        Assert.All(rows.Where(row => row.Value.Contains("catalog rule", StringComparison.OrdinalIgnoreCase)), row =>
            Assert.True(catalogued.Contains(row.Key), $"The rule map says catalog rule {row.Key} enforces its row, but the catalog has no such rule."));
        Assert.All(catalogued.Where(id => MapId().IsMatch(id)), id =>
            Assert.True(rows.ContainsKey(id), $"Catalog rule {id} carries a rule-map id the map has no row for."));
    }

    [Fact]
    public void Section_EndsAtTheNextHeadingOfTheSameOrHigherLevel()
    {
        const string text = "# Top\n\n## One\nfirst\n### Inner\nstill one\n## Two\nsecond\n";

        Assert.Equal("## One\nfirst\n### Inner\nstill one\n", ProseRuleCatalog.Section(text, "One"));
        Assert.Equal("# Top\n\n## Two\nsecond\n", ProseRuleCatalog.WithoutSection(text, "One"));
        Assert.Null(ProseRuleCatalog.Section(text, "Three"));
    }

    [Fact]
    public void Section_IgnoresHeadingsInsideFencedCode()
    {
        const string text = "## Real\nbody\n```\n## Fake\n```\nmore\n## Next\n";

        Assert.Equal("## Real\nbody\n```\n## Fake\n```\nmore\n", ProseRuleCatalog.Section(text, "Real"));
    }

    [Fact]
    public void Parts_KeepsWholeSectionsAndCoversTheDocument()
    {
        string text = "# T\nintro\n## A\n" + new string('a', 50) + "\n## B\n" + new string('b', 50) + "\n## C\nc\n";

        IReadOnlyList<string> parts = ProseRuleCatalog.Parts(text, 80);

        Assert.Equal(text, string.Concat(parts));
        Assert.True(parts.Count > 1);
        Assert.All(parts.Skip(1), part => Assert.StartsWith("## ", part, StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RefusesAnUnknownKeyByName()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "version: 1\nconfidence-floor: 0.8\nrules:\n  - id: x\n    statement: s\n    stated-in: [a.md]\n    controls: [b.md]\n    phrase: pinned\n");

            InvalidDataException error = Assert.Throws<InvalidDataException>(() => ProseRuleCatalog.Load(path));

            Assert.Contains("unknown key 'phrase'", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    [GeneratedRegex("^(?:[A-G][0-9]+|[A-G][0-9]+(?:-[a-z0-9]+)+|[a-z0-9]+(?:-[a-z0-9]+)+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-G][0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MapId();

    [GeneratedRegex(@"^\| (?<id>[A-G][0-9]+) \|.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MapRow();
}
