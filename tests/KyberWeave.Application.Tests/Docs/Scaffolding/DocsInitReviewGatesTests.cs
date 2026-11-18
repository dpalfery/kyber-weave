using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review.Catalogue;
using KyberWeave.Tests;
using KyberWeave.Tests.Corpus;
using KyberWeave.Tests.Fakes;
using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
///     <c>docs init</c> appends the rendered <c>review:</c> block when the host configuration has
///     none, never changes an existing block, and honours the opt-out. Each family fixture proves
///     the written section byte for byte against its approved <c>expected-review.yml</c>. The scaffolder runs
///     over an in-memory checkout; the command's reporting is in <c>DocsInitReviewGatesReportTests</c> and the
///     real toolchain run in the integration project.
/// </summary>
public sealed class DocsInitReviewGatesTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ConfigRelativePath = ".kyber-weave/kyber-weave.yml";

    private readonly MemoryScaffoldHost _host = new();

    /// <summary>One argument per gate-catalogue family fixture directory.</summary>
    public static IEnumerable<object[]> FamilyFixtureNames()
    {
        yield return new object[] { "csharp" };
        yield return new object[] { "go" };
        yield return new object[] { "python" };
        yield return new object[] { "rust" };
        yield return new object[] { "typescript" };
    }

    /// <summary>
    ///     The written <c>review:</c> section must equal the family's approved expected file.
    ///     A hand edit to the catalogue that changes what a host receives fails here rather
    ///     than silently shipping different gates to the next host that runs <c>docs init</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(FamilyFixtureNames))]
    public void DocsInitWritesTheApprovedReviewBlock(string family)
    {
        NewFixtureRepo(family, TechnologiesFor(family));

        _host.Scaffold();

        string written = NormalizeVersionStamp(ReadReviewSection());
        string expected = NormalizeVersionStamp(Corpus.ReadText(ExpectedReviewPath(family)));
        Assert.Equal(expected, written);
    }

    /// <summary>
    ///     A host-owned <c>review:</c> section is never rewritten, with or without
    ///     <c>--force</c>. Regenerating it would discard gates the operator tuned by hand. The
    ///     run adds the <c>mcp:</c> declaration the file lacks and nothing else.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExistingReviewSectionSurvivesDocsInitUnchanged(bool force)
    {
        NewFixtureRepo("go", ["go"]);
        string original = HandOwnedConfigWithReview();
        _host.Write(ConfigRelativePath, original);

        // The hand-owned block must load, or the scaffold preflight would reject it and
        // this test would prove nothing about preservation.
        KyberWeaveConfig loaded = _host.LoadConfig();
        Assert.Contains(
            loaded.Review.Gates, gate => string.Equals(gate.Id, "hand-owned-council", StringComparison.Ordinal));

        _host.Scaffold(force: force);

        // The operator's file, hand-owned review section included, is an exact prefix of what
        // was written; the declaration is the only thing appended after it.
        Assert.Equal(original + McpWiringFixture.AppendedDeclaration("\n"), _host.Read(ConfigRelativePath));
    }

    /// <summary>
    ///     The second run finds <c>review:</c> and writes nothing, so the host configuration is
    ///     byte-identical to the first run's result. A diff on every re-run would make adoption
    ///     look perpetually dirty.
    /// </summary>
    [Fact]
    public void RunningDocsInitTwiceLeavesTheHostConfigByteIdentical()
    {
        NewFixtureRepo("go", ["go"]);

        _host.Scaffold();
        string first = _host.Read(ConfigRelativePath);

        _host.Scaffold();
        string second = _host.Read(ConfigRelativePath);

        Assert.False(
            string.IsNullOrEmpty(ReadReviewSection()),
            "The first run wrote no review: section, so the second run proves nothing about idempotency.");
        Assert.Equal(first, second);
    }

    /// <summary>
    ///     Adding the section must not touch any other byte: comments, key order, and the
    ///     file's newline style belong to the operator. Both newline styles are covered because
    ///     the append uses the file's existing style. The <c>mcp:</c> declaration is appended
    ///     ahead of the review section, so everything before <c>review:</c> is the operator's
    ///     bytes plus exactly that declaration.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AnExistingConfigWithoutAReviewSectionKeepsEveryOtherByte(string newline)
    {
        NewFixtureRepo("go", ["go"]);
        string original = string.Join(
            newline, "# Ours, not the tool's.", "ontology:", "  docs-root: docs", "  technologies:", "    - go",
            string.Empty);
        _host.Write(ConfigRelativePath, original);

        _host.Scaffold();

        string updated = _host.Read(ConfigRelativePath);
        Assert.Contains(newline + "review:", updated, StringComparison.Ordinal);
        int reviewIndex = updated.IndexOf(newline + "review:", StringComparison.Ordinal);
        Assert.Equal(
            original.TrimEnd('\r', '\n') + McpWiringFixture.AppendedDeclaration(newline),
            updated.Substring(0, reviewIndex));
    }

    /// <summary>
    ///     The file <c>docs init</c> writes must load through <c>ConfigFileReader</c>
    ///     with no configuration error. A catalogue that cannot load is a bug the tests catch
    ///     first, not the host's first review.
    /// </summary>
    [Fact]
    public void TheWrittenHostConfigLoadsWithoutAConfigurationError()
    {
        NewFixtureRepo("go", ["go"]);

        _host.Scaffold();

        KyberWeaveConfigLoadResult loaded = _host.Configs.TryLoad(_host.Root);
        Assert.True(
            loaded.Success,
            $"docs init wrote a host config that does not load: {loaded.Error ?? "no error"}");
        Assert.DoesNotContain(
            KyberWeaveConfigDiagnosticCodes.LoadError, loaded.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.NotNull(loaded.Config);
        Assert.NotEmpty(loaded.Config.Review.Gates);
    }

    /// <summary>
    ///     The whole-suite gate <c>docs init</c> writes for a family carries <c>full-suite: true</c>, and the
    ///     gates that run one check do not.
    /// </summary>
    /// <remarks>
    ///     Without the key, a repository scaffolded by <c>docs init</c> hands its developer agents the whole
    ///     suite that ADR 0059 reserves for the council and CI. Marking every gate full-suite would be the
    ///     opposite defect: the format check would be refused too. Which gate is the whole-suite one is read
    ///     from the catalogue's own <c>test</c> role rather than from a list here, so a family that grows a
    ///     second full-suite gate is covered.
    /// </remarks>
    [Theory]
    [MemberData(nameof(FamilyFixtureNames))]
    public void DocsInitWritesFullSuiteOnTheWholeSuiteGateAndOnNoOther(string family)
    {
        NewFixtureRepo(family, TechnologiesFor(family));

        _host.Scaffold();

        GateCatalogueFamily catalogue = GateCatalogueRenderer.LoadEmbedded()
            .Single(candidate => string.Equals(candidate.Family, family, StringComparison.Ordinal));
        HashSet<string> wholeSuite = catalogue.Gates
            .Where(gate => string.Equals(gate.Role, "test", StringComparison.Ordinal))
            .Select(gate => family + "-" + gate.Id)
            .ToHashSet(StringComparer.Ordinal);

        KyberWeaveConfig config = _host.LoadConfig();

        Assert.NotEmpty(config.Review.Gates);
        string[] wrong = config.Review.Gates
            .Where(gate => gate.FullSuite != wholeSuite.Contains(gate.Id))
            .Select(gate => $"'{gate.Id}' is written with full-suite: {(gate.FullSuite ? "true" : "false")}")
            .ToArray();

        Assert.True(
            wrong.Length == 0,
            $"docs init wrote the wrong full-suite status for family '{family}'. A gate that runs the whole " +
            "suite must declare full-suite: true and one that runs a single check must not:" +
            Environment.NewLine + string.Join(Environment.NewLine, wrong));
        Assert.True(
            config.Review.Gates.Any(gate => gate.FullSuite),
            $"docs init wrote no gate carrying full-suite: true for family '{family}', so a host scaffolded " +
            "from this catalogue can run the whole suite through a gate that ADR 0059 reserves for the council.");
    }

    /// <summary>
    ///     Every key the generated block carries is a key this release reads, so a fresh host loads without a
    ///     <c>KW-CONFIG-002</c> warning.
    /// </summary>
    /// <remarks>
    ///     A catalogue that writes a key the shipped binary does not map ships a host a warning it cannot fix
    ///     and cannot read: the gate it thought it had is not the gate it has. The load reports such a key as a
    ///     warning rather than failing, so it is exactly the kind of defect that reaches a host unnoticed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(FamilyFixtureNames))]
    public void EveryKeyTheGeneratedReviewBlockCarriesIsOneThisReleaseReads(string family)
    {
        NewFixtureRepo(family, TechnologiesFor(family));

        _host.Scaffold();

        KyberWeaveConfig config = _host.LoadConfig();

        Assert.True(
            config.Review.Warnings.Count == 0,
            $"docs init wrote a review block this release reports as containing unknown keys " +
            $"({KyberWeaveConfigDiagnosticCodes.Warning}): {string.Join("; ", config.Review.Warnings)}");
    }

    private static IReadOnlyList<string> TechnologiesFor(string family)
    {
        GateCatalogueFamily catalogue = GateCatalogueRenderer.LoadEmbedded()
            .Single(candidate => string.Equals(candidate.Family, family, StringComparison.Ordinal));
        return catalogue.Technologies;
    }

    /// <summary>Seeds the family's clean fixture and a host configuration declaring its technologies.</summary>
    private void NewFixtureRepo(string family, IReadOnlyList<string> technologies) =>
        DocsInitFixtureSeed.Family(_host.Memory, _host.Root, family, technologies);

    private static string ExpectedReviewPath(string family)
    {
        return $"tests/fixtures/gate-catalogue/{family}/expected-review.yml";
    }

    /// <summary>
    ///     The header comment stamps the build version, which carries the commit hash; blank it
    ///     so the fixtures stay valid on every commit.
    /// </summary>
    private static string NormalizeVersionStamp(string yaml)
    {
        return Regex.Replace(yaml, "written by Kyber-Weave \\S+", "written by Kyber-Weave <version>");
    }

    /// <summary>
    ///     The written <c>review:</c> section: from the top-level <c>review</c> key (a commented
    ///     <c># review:</c> does not count) to the end of the file. Empty when absent.
    /// </summary>
    private string ReadReviewSection()
    {
        string text = _host.Read(ConfigRelativePath).ReplaceLineEndings("\n");
        string[] lines = (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
        for (int index = 0; index < lines.Length; index++)
            if (Regex.IsMatch(lines[index], "^review[ \\t]*:"))
                return string.Join("\n", lines, index, lines.Length - index) + "\n";

        return string.Empty;
    }

    /// <summary>
    ///     A hand-owned host configuration: operator comments, the go stack, and a review
    ///     block with ids the catalogue never emits, so every catalogue gate for go is missing
    ///     from it.
    /// </summary>
    private static string HandOwnedConfigWithReview()
    {
        return """
               # Ours, not the tool's.
               ontology:
                 docs-root: docs
                 technologies:
                   - go
               review:
                 gates:
                   - id: hand-owned-council
                     run: [git, --version]
                 task-gates:
                   go-dev:
                     - id: hand-owned-task
                       include: ["**/*.go"]
                       run: [git, --version]
               """;
    }
}
