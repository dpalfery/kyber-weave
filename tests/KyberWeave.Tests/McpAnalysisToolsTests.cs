using System.Reflection;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T14 RED — MCP analysis tools are capped, stable, conversational reads over the
/// repository's current configured corpus and never expose a write capability.
/// </summary>
public sealed class McpAnalysisToolsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void AnalysisCandidates_KindFilterAndCursor_PageStableOrderedCandidates()
    {
        var reader = new StubAnalysisReader(
            Result(
                Candidate("candidate-c", AnalysisRuleKind.Conflict),
                Candidate("candidate-b", AnalysisRuleKind.Duplicate),
                Candidate("candidate-a", AnalysisRuleKind.Duplicate)));
        var tools = Tools(reader);

        var first = tools.AnalysisCandidates(
            kind: "duplicate",
            cursor: null,
            limit: 1,
            charBudget: 4_000);
        var second = tools.AnalysisCandidates(
            kind: "duplicate",
            cursor: "candidate-a",
            limit: 1,
            charBudget: 4_000);

        Assert.Contains("candidate: candidate-a", first, StringComparison.Ordinal);
        Assert.Contains("next cursor: candidate-a", first, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate-b", first, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate-c", first, StringComparison.Ordinal);
        Assert.Contains("candidate: candidate-b", second, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate-a", second, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate-c", second, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisCandidates_ExcessiveLimitAndBudget_EnforcesHardConversationalCaps()
    {
        var candidates = Enumerable.Range(0, 40)
            .Select(index => Candidate($"candidate-{index:D2}", AnalysisRuleKind.Duplicate))
            .ToArray();
        var tools = Tools(new StubAnalysisReader(Result(candidates)));

        var response = tools.AnalysisCandidates(
            kind: null,
            cursor: null,
            limit: int.MaxValue,
            charBudget: int.MaxValue);

        Assert.True(response.Length <= 12_000, $"MCP response contained {response.Length} characters.");
        Assert.InRange(Occurrences(response, "candidate: "), 1, 20);
        Assert.Contains("next cursor:", response, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisCandidates_SmallBudget_KeepsMetricsAndLineEvidenceInsideBudget()
    {
        var candidate = Candidate(
            "candidate-budget",
            AnalysisRuleKind.Terminology,
            term: "loop",
            claimText: new string('x', 2_000));
        var tools = Tools(new StubAnalysisReader(Result(candidate)));

        var response = tools.AnalysisCandidates(
            kind: "terminology",
            cursor: null,
            limit: 20,
            charBudget: 600);

        Assert.True(response.Length <= 600, $"MCP response contained {response.Length} characters.");
        Assert.Contains("metrics:", response, StringComparison.Ordinal);
        Assert.Contains("extracted claims: 2", response, StringComparison.Ordinal);
        Assert.Contains("candidate: candidate-budget", response, StringComparison.Ordinal);
        Assert.Contains("docs/left.md:10-10", response, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 1_000), response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("duplicate, conflict")]
    public void AnalysisCandidates_NumericOrCompositeKind_ReturnsUnknownKind(string kind)
    {
        var tools = Tools(new StubAnalysisReader(Result(Candidate("candidate-a", AnalysisRuleKind.Duplicate))));

        var response = tools.AnalysisCandidates(kind: kind, cursor: null, limit: 20, charBudget: 4_000);

        Assert.Contains($"Unknown documentation-analysis kind '{kind}'", response, StringComparison.Ordinal);
        Assert.DoesNotContain("No 99 candidates", response, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate-a", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Glossary_KnownAndUnknownTerms_ReturnConversationalReadOnlyResults()
    {
        var reader = new StubAnalysisReader(
            Result(),
            new Dictionary<string, GlossaryLookupResult>(StringComparer.OrdinalIgnoreCase)
            {
                ["loop"] = new GlossaryLookupResult(
                    "loop",
                    [new GlossarySense(
                        "loop-gameplay",
                        GlossarySenseStatus.Approved,
                        "The gameplay update cycle.",
                        ["component:Gameplay"],
                        ["gameplay loop"],
                        ["claim-gameplay"])])
            });
        var tools = Tools(reader);

        var known = tools.Glossary("LOOP");
        var unknown = tools.Glossary("missing-term");

        Assert.Contains("loop-gameplay", known, StringComparison.Ordinal);
        Assert.Contains("approved", known, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The gameplay update cycle.", known, StringComparison.Ordinal);
        Assert.Contains("component:Gameplay", known, StringComparison.Ordinal);
        Assert.Contains("gameplay loop", known, StringComparison.Ordinal);
        Assert.Contains("No glossary senses", unknown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", unknown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Glossary_LongUnknownTerm_EnforcesHardConversationalCap()
    {
        var tools = Tools(new StubAnalysisReader(Result()));
        var term = new string('x', 20_000);

        var response = tools.Glossary(term);

        Assert.True(response.Length <= 12_000, $"MCP response contained {response.Length} characters.");
        Assert.Contains("No glossary senses", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void McpAnalysisTools_ExposeOnlyThePinnedReadParameters()
    {
        var candidates = typeof(DocsTools).GetMethod(nameof(DocsTools.AnalysisCandidates));
        var glossary = typeof(DocsTools).GetMethod(nameof(DocsTools.Glossary));

        Assert.NotNull(candidates);
        Assert.Equal(typeof(string), candidates.ReturnType);
        Assert.Equal(
            ["kind", "cursor", "limit", "charBudget"],
            candidates.GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(20, candidates.GetParameters()[2].DefaultValue);
        Assert.Equal(12_000, candidates.GetParameters()[3].DefaultValue);
        Assert.Equal("docs_analysis_candidates", McpToolName(candidates));

        Assert.NotNull(glossary);
        Assert.Equal(typeof(string), glossary.ReturnType);
        Assert.Equal(["term"], glossary.GetParameters().Select(parameter => parameter.Name));
        Assert.Equal("docs_glossary", McpToolName(glossary));
        Assert.DoesNotContain(
            typeof(DocsTools).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name.Contains("Write", StringComparison.OrdinalIgnoreCase)
                || method.GetParameters().Any(parameter =>
                    parameter.Name?.Contains("write", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public void RepositoryAnalysisReader_UsesConfiguredRootAndDoesNotCreateUnsafeCacheState()
    {
        Write(".kyber-weave/kyber-weave.yml", """
            ontology:
              docs-root: [knowledge]
            docs-analysis:
              statuses: [current]
              glossary-path: knowledge/terms.md
            """);
        Write("knowledge/first.md", Document("reference/first", "current"));
        Write("knowledge/second.md", Document("reference/second", "current"));
        Write("knowledge/draft.md", Document("reference/draft", "draft"));
        Write("knowledge/terms.md", GlossaryMarkdown());
        var reader = new RepositoryDocsAnalysisReader(_temp.Path);

        var analysis = reader.Analyze();
        var glossary = reader.LookupGlossary("loop");

        Assert.Equal(2, analysis.Metrics.ExtractedClaims);
        Assert.Single(analysis.Candidates, candidate => candidate.IsExact);
        Assert.Equal("loop", glossary.Term);
        Assert.Single(glossary.Senses);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, ".kyber-weave", "cache")));
    }

    [Fact]
    public void RepositoryAnalysisReader_UnavailableCodeGraph_ReportsOneDegradedWarning()
    {
        Write(".kyber-weave/kyber-weave.yml", """
            ontology:
              docs-root: [knowledge]
            docs-analysis:
              statuses: [current]
            """);
        Write("knowledge/first.md", Document("reference/first", "current"));
        var reader = new RepositoryDocsAnalysisReader(_temp.Path);

        var analysis = reader.Analyze();

        var warning = Assert.Single(analysis.Diagnostics.Items, finding =>
            finding.Code == DocumentationAnalyzer.CodeGraphUnavailableRuleCode);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("bounded lexical search", warning.Hint, StringComparison.Ordinal);
    }

    public void Dispose() => _temp.Dispose();

    private DocsTools Tools(IDocsAnalysisReader reader)
    {
        var host = new DocumentIndexHost(
            _temp.Path,
            () => FakeCodeGraphResolver.WithSymbols(),
            () => new DocumentSet { Documents = [] },
            "docs");
        return new DocsTools(host, reader);
    }

    private static DocumentationAnalysisResult Result(params AnalysisCandidate[] candidates) =>
        new(
            candidates,
            new DiagnosticReport(),
            new AnalysisMetrics(
                ExtractedClaims: 2,
                GraphComparisons: 3,
                LexicalComparisons: 4,
                EmbeddingComparisons: 0,
                GraphCandidates: 1,
                LexicalCandidates: 2,
                EmbeddingCandidates: 0,
                Truncated: false));

    private static AnalysisCandidate Candidate(
        string id,
        AnalysisRuleKind kind,
        string? term = null,
        string claimText = "The runtime retains reviewed documentation evidence for later analysis.") =>
        new(
            id,
            kind,
            [
                Claim("left", "docs/left.md", 10, claimText),
                Claim("right", "docs/right.md", 20, claimText + " Additional context.")
            ],
            new CandidateScore(0.75, 0.88, 1),
            Term: term,
            Sources: [CandidateSourceKind.Graph, CandidateSourceKind.Lexical]);

    private static Claim Claim(string id, string path, int line, string text) => new(
        ClaimKind.Paragraph,
        text,
        "Behavior\n" + text,
        "content-" + id,
        "context-" + id,
        "reference/" + id,
        "Runtime",
        "Behavior",
        path,
        line,
        line,
        IgnoreRule.None);

    private static string McpToolName(MethodInfo method) =>
        method.CustomAttributes
            .Single(attribute => attribute.AttributeType.Name == "McpServerToolAttribute")
            .NamedArguments
            .Single(argument => argument.MemberName == "Name")
            .TypedValue.Value?.ToString() ?? string.Empty;

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Document(string id, string status) => $$"""
        ---
        id: {{id}}
        title: {{id}}
        doc-type: reference
        status: {{status}}
        owner: Maintainers
        last-reviewed: 2026-08-12
        ---

        # Reference

        ## Behavior

        The runtime retains reviewed documentation evidence for later analysis.
        """;

    private static string GlossaryMarkdown() => """
        ---
        id: reference/glossary
        title: Glossary
        doc-type: reference
        status: needs-review
        owner: Maintainers
        last-reviewed: 2026-08-12
        ---

        # Glossary

        ## loop

        | Sense ID | Status | Definition | Scope | Aliases |
        |---|---|---|---|---|
        | loop-proposed | proposed |  | component:Gameplay | gameplay loop |
        """;

    private sealed class StubAnalysisReader(
        DocumentationAnalysisResult result,
        IReadOnlyDictionary<string, GlossaryLookupResult>? glossary = null) : IDocsAnalysisReader
    {
        public DocumentationAnalysisResult Analyze() => result;

        public GlossaryLookupResult LookupGlossary(string term) =>
            glossary?.GetValueOrDefault(term)
            ?? new GlossaryLookupResult(term.Trim().ToLowerInvariant(), []);
    }
}
