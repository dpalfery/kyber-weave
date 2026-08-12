using KyberWeave.Core.Configuration;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Specifies the managed glossary as a conservative file merger. Source documentation is
/// never changed; only generated proposals in the configured reference document are owned
/// by the feature.
/// </summary>
public sealed class ManagedGlossaryTests
{
    private static readonly DateTimeOffset Today = new(2026, 8, 11, 15, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "docs/glossary.md")]
    [InlineData("components/gameplay/docs/terms.md", "components/gameplay/docs/terms.md")]
    public void Preview_DefaultOrConfiguredPath_UsesConfiguredCorpusLocationWithoutWriting(
        string? configuredPath,
        string expectedPath)
    {
        using var repository = Repository(docsRoots: ["docs", "components/gameplay/docs"]);
        var service = Service(repository, glossaryPath: configuredPath);

        var result = service.Preview([Proposal("loop", "component:Gameplay", "claim-1")]);

        Assert.Equal(expectedPath, result.RelativePath);
        Assert.False(result.Written);
        Assert.False(File.Exists(repository.FullPath(expectedPath)));
        Assert.Contains("## loop", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_MissingGlossary_CreatesConformantReferenceUsingFirstCatalogOwnerAndUtcDate()
    {
        using var repository = Repository();
        var service = Service(repository);

        var result = service.Write([Proposal("loop", "component:Gameplay", "claim-1")]);

        Assert.True(result.Written);
        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("doc-type: reference", markdown, StringComparison.Ordinal);
        Assert.Contains("status: needs-review", markdown, StringComparison.Ordinal);
        Assert.Contains("owner: Gameplay maintainers", markdown, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: 2026-08-11", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_CatalogHasNoDataOwner_FailsBeforeCreatingGlossary()
    {
        using var repository = Repository(catalogRows: []);
        var service = Service(repository);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.Write([Proposal("loop", "component:Gameplay", "claim-1")]));

        Assert.Contains("owner", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(repository.FullPath("docs/glossary.md")));
    }

    [Fact]
    public void Write_NewProposal_UsesExactTableShapeStatusScopeAndMarkedEvidence()
    {
        using var repository = Repository();
        var service = Service(repository);

        service.Write([Proposal("loop", "component:Gameplay", "claim-1", aliases: ["gameplay loop"])]);

        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("| Sense ID | Status | Definition | Scope | Aliases |", markdown, StringComparison.Ordinal);
        Assert.Matches(@"\| loop-[a-f0-9]{8} \| proposed \|  \| component:Gameplay \| gameplay loop \|", markdown);
        Assert.Contains("<!-- kyber-weave:glossary-evidence:start", markdown, StringComparison.Ordinal);
        Assert.Contains("claim-1", markdown, StringComparison.Ordinal);
        Assert.Contains("<!-- kyber-weave:glossary-evidence:end", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ExistingGlossary_PreservesOwnerDateHumanProseAndReviewedRows()
    {
        using var repository = Repository().WriteGlossary(ExistingGlossary(
            """
            Human introduction that must stay intact.

            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-approved1 | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |
            | loop-rejected1 | rejected | Not a useful meaning. | component:Agents | churn loop |

            Human notes that must also stay intact.
            """));
        var service = Service(repository);

        service.Write([Proposal("loop", "component:Runtime", "claim-2")]);

        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("owner: Human owner", markdown, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: 2026-07-01", markdown, StringComparison.Ordinal);
        Assert.Contains("Human introduction that must stay intact.", markdown, StringComparison.Ordinal);
        Assert.Contains("Human notes that must also stay intact.", markdown, StringComparison.Ordinal);
        Assert.Contains("| loop-approved1 | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |", markdown, StringComparison.Ordinal);
        Assert.Contains("| loop-rejected1 | rejected | Not a useful meaning. | component:Agents | churn loop |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ProposalEvidenceChanges_RefreshesEvidenceAndDemotesCurrentDocument()
    {
        using var repository = Repository();
        var service = Service(repository);
        service.Write([Proposal("loop", "component:Gameplay", "old-claim", aliases: ["gameplay loop"])]);
        repository.ReplaceInGlossary("status: needs-review", "status: current");
        repository.ReplaceInGlossary("last-reviewed: 2026-08-11", "last-reviewed: 2026-07-01");

        var result = service.Write([Proposal("loop", "component:Gameplay", "new-claim", aliases: ["gameplay loop"])]);

        Assert.True(result.Changed);
        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("status: needs-review", markdown, StringComparison.Ordinal);
        Assert.Contains("new-claim", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("old-claim", markdown, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: 2026-07-01", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_UntouchedGeneratedProposalLosesAllEvidence_RemovesProposal()
    {
        using var repository = Repository();
        var service = Service(repository);
        service.Write([Proposal("loop", "component:Gameplay", "old-claim", aliases: ["gameplay loop"])]);

        service.Write([]);

        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.DoesNotContain("| proposed |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("old-claim", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ProposalWithoutOwnershipFingerprintLosesEvidence_PreservesRow()
    {
        using var repository = Repository().WriteGlossary(ExistingGlossary(
            """
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a1b2c3d4 | proposed |  | component:Gameplay | gameplay loop |

            <!-- kyber-weave:glossary-evidence:start sense="loop-a1b2c3d4" -->
            - old-claim
            <!-- kyber-weave:glossary-evidence:end -->
            """));

        Service(repository).Write([]);

        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("loop-a1b2c3d4", markdown, StringComparison.Ordinal);
        Assert.Contains("old-claim", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_GeneratedProposalEvidenceWasHumanEdited_PreservesRowAndEvidence()
    {
        using var repository = Repository();
        var service = Service(repository);
        service.Write([Proposal("loop", "component:Gameplay", "claim-1", aliases: ["gameplay loop"])]);
        repository.ReplaceInGlossary("- claim-1", "- human evidence note");

        service.Write([]);

        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("| proposed |", markdown, StringComparison.Ordinal);
        Assert.Contains("human evidence note", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_FencedManagedHeadingAndTableExample_PreservesExampleByteForByte()
    {
        const string example = """
            ```markdown
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | example-loop | approved | Example only. | component:Gameplay | example |
            ```
            """;
        using var repository = Repository().WriteGlossary(ExistingGlossary(example));

        Service(repository).Write([Proposal("loop", "component:Agents", "claim-1")]);

        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains(example, markdown, StringComparison.Ordinal);
        Assert.True(markdown.LastIndexOf("## loop", StringComparison.Ordinal)
                    > markdown.IndexOf("\n```\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RealTermWithFencedManagedTableAndEvidence_IgnoresFencedLookalikes()
    {
        const string fencedExample = """
            ```markdown
            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | example-loop | accepted | Example only. | team:Example | example |

            <!-- kyber-weave:glossary-evidence:start sense="missing-example" -->
            - example-claim
            <!-- kyber-weave:glossary-evidence:end -->
            ```
            """;
        using var repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            {{fencedExample}}

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |
            """));

        var report = Service(repository).Validate();

        Assert.Empty(report.Items);
    }

    [Fact]
    public void PreviewAndWrite_RealTermWithFencedManagedTableAndEvidence_PreserveFencedBytes()
    {
        const string fencedExample = """
            ```markdown
            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | example-loop | accepted | Example only. | team:Example | example |

            <!-- kyber-weave:glossary-evidence:start sense="missing-example" -->
            - example-claim
            <!-- kyber-weave:glossary-evidence:end -->
            ```
            """;
        using var repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            {{fencedExample}}

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |
            """));
        var service = Service(repository);
        var proposals = new[] { Proposal("loop", "component:Agents", "claim-1") };

        var preview = service.Preview(proposals);
        var written = service.Write(proposals);

        Assert.Contains(fencedExample, preview.Markdown, StringComparison.Ordinal);
        Assert.Contains(fencedExample, written.Markdown, StringComparison.Ordinal);
        Assert.Contains(fencedExample, File.ReadAllText(repository.FullPath("docs/glossary.md")), StringComparison.Ordinal);
        Assert.True(written.Markdown.LastIndexOf("| proposed |", StringComparison.Ordinal)
                    > written.Markdown.LastIndexOf("```", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Human-authored definition", "component:Gameplay", "game cycle")]
    [InlineData("", "component:Gameplay; code-ref:Runner.Loop", "human alias")]
    public void Write_EditedProposalLosesEvidence_PreservesHumanEdits(
        string definition,
        string scope,
        string aliases)
    {
        using var repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a1b2c3d4 | proposed | {{definition}} | {{scope}} | {{aliases}} |

            <!-- kyber-weave:glossary-evidence:start sense="loop-a1b2c3d4" -->
            - old-claim
            <!-- kyber-weave:glossary-evidence:end -->
            """));
        var service = Service(repository);

        service.Write([]);

        var markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("loop-a1b2c3d4", markdown, StringComparison.Ordinal);
        Assert.Contains(definition, markdown, StringComparison.Ordinal);
        Assert.Contains(scope, markdown, StringComparison.Ordinal);
        Assert.Contains(aliases, markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("pending")]
    [InlineData("CURRENT")]
    public void Validate_UnknownSenseStatus_ReturnsGlossaryDiagnostic(string status)
    {
        using var repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a1b2c3d4 | {{status}} | A definition. | component:Gameplay | gameplay loop |
            """));

        var report = Service(repository).Validate();

        Assert.Contains(report.Items, item => item.Code == ManagedGlossaryService.ValidationRuleCode);
        Assert.All(report.Items, item => Assert.Equal("KW-DOC-GLOSSARY-001", item.Code));
    }

    [Theory]
    [InlineData("", "component:Gameplay")]
    [InlineData("A definition.", "")]
    [InlineData("A definition.", "team:Gameplay")]
    [InlineData("A definition.", "component:Unknown")]
    public void Validate_InvalidApprovedSense_ReturnsGlossaryDiagnostic(string definition, string scope)
    {
        using var repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a1b2c3d4 | approved | {{definition}} | {{scope}} | gameplay loop |
            """));

        var report = Service(repository).Validate();

        Assert.Contains(report.Items, item => item.Code == "KW-DOC-GLOSSARY-001");
    }

    [Fact]
    public void Load_ApprovedAndRejectedRows_ReturnsOnlyApprovedAnalysisSenses()
    {
        using var repository = Repository().WriteGlossary(ExistingGlossary(
            """
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |
            | loop-agent | rejected | Rejected meaning. | component:Agents | churn loop |
            """));

        AnalysisGlossary glossary = Service(repository).Load().AnalysisGlossary;

        var sense = Assert.Single(glossary.Senses);
        Assert.Equal("loop-gameplay", sense.Id);
        Assert.Equal("The gameplay update cycle.", sense.Definition);
        Assert.Equal(["component:Gameplay"], sense.Scopes);
        Assert.Equal(["gameplay loop"], sense.Aliases);
    }

    [Fact]
    public void Lookup_TermIsCaseInsensitive_ReturnsAllStatusesAndParsedScopes()
    {
        using var repository = Repository().WriteGlossary(ExistingGlossary(
            """
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay; code-ref:Game.Run | gameplay loop |
            | loop-agent | proposed |  | component:Agents | churn loop |
            """));

        var result = Service(repository).Lookup("LOOP");

        Assert.Equal("loop", result.Term);
        Assert.Equal(2, result.Senses.Count);
        Assert.Contains(result.Senses, sense => sense.Status == GlossarySenseStatus.Approved
            && sense.Scopes.SequenceEqual(["component:Gameplay", "code-ref:Game.Run"]));
        Assert.Contains(result.Senses, sense => sense.Status == GlossarySenseStatus.Proposed);
    }

    [Theory]
    [InlineData("""
        ---
        id: reference/glossary
        title: Glossary
        doc-type: reference
        status: needs-review
        owner: Human owner
        last-reviewed: 2026-07-01

        # Glossary

        ## loop

        | Sense ID | Status | Definition | Scope | Aliases |
        |---|---|---|---|---|
        | loop-a | approved | Definition. | component:Gameplay | gameplay loop |
        """)]
    [InlineData("""
        ---
        id: reference/glossary
        title: Glossary
        doc-type: reference
        status: needs-review
        owner: Human owner
        last-reviewed: 2026-07-01
        ---

        # Glossary

        ## loop

        | Sense ID | Status | Definition | Scope | Aliases |
        |---|---|---|---|---|
        loop-a | approved | Definition. | component:Gameplay | gameplay loop
        """)]
    public void Validate_MalformedFrontmatterOrSenseRow_FailsClosed(string markdown)
    {
        using var repository = Repository().WriteGlossary(markdown);

        var report = Service(repository).Validate();

        Assert.Contains(report.Items, item => item.Code == "KW-DOC-GLOSSARY-001");
    }

    [Fact]
    public void Write_FirstCatalogOwnerContainsYamlPunctuation_RoundTripsExactOwner()
    {
        const string owner = "Docs: Core # on-call";
        using var repository = Repository(catalogRows:
        [
            $"| Gameplay | Application | `src/Game` | [README](x) | [docs](y) | {owner} | 2026-08-01 | Current |"
        ]);

        Service(repository).Write([Proposal("loop", "component:Gameplay", "claim-1")]);

        var set = new DocumentLoader(repository.Root, repository.Ontology).Load();
        var glossary = Assert.Single(set.Documents, document => document.RelativePath == "docs/glossary.md");
        Assert.Null(glossary.ParseError);
        Assert.Equal(owner, glossary.Frontmatter.Owner);
    }

    [Fact]
    public void DocsValidate_InvalidManagedGlossary_ReturnsGlossaryOperationalError()
    {
        using var repository = Repository(catalogRows:
        [
            "| Gameplay | Application | `src/Game` | [README](x) | [docs](y) | Gameplay maintainers | 2026-08-01 | Current |",
            "| System | System | repository root | [README](x) | [docs](y) | Maintainers | 2026-08-01 | Current |"
        ]).WriteGlossary("""
            ---
            id: reference/glossary
            title: Glossary
            doc-type: reference
            status: needs-review
            owner: Gameplay maintainers
            last-reviewed: 2026-07-01
            ---

            # Glossary

            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a | accepted | Definition. | component:Gameplay | gameplay loop |
            """);
        repository.WriteConfig("""
            ontology:
              docs-root: docs
            docs-analysis:
              glossary-path: docs/glossary.md
            """);
        var settings = new DocsSettings { Path = repository.Root, Format = "json" };

        var exitCode = new DocsValidateCommand().Execute(null!, settings);

        Assert.Equal(1, exitCode);
    }

    private static ManagedGlossaryService Service(GlossaryRepository repository, string? glossaryPath = null) =>
        new(
            repository.Root,
            new KyberWeaveConfig
            {
                Ontology = repository.Ontology,
                DocsAnalysis = new DocsAnalysisConfig { GlossaryPath = glossaryPath }
            },
            new FixedTimeProvider(Today));

    private static GlossaryProposal Proposal(
        string term,
        string scope,
        string evidenceId,
        IReadOnlyList<string>? aliases = null) =>
        new(term, "", [scope], aliases ?? [], [evidenceId]);

    private static GlossaryRepository Repository(
        IReadOnlyList<string>? docsRoots = null,
        IReadOnlyList<string>? catalogRows = null) =>
        new(docsRoots ?? ["docs"], catalogRows ??
        [
            "| Gameplay | Application | `src/Game` | [README](x) | [docs](y) | Gameplay maintainers | 2026-08-01 | Current |",
            "| Agents | Tool | `src/Agents` | [README](x) | [docs](y) | Agent maintainers | 2026-08-01 | Current |"
        ]);

    private static string ExistingGlossary(string body, string status = "needs-review") => $$"""
        ---
        id: reference/glossary
        title: Glossary
        doc-type: reference
        status: {{status}}
        owner: Human owner
        last-reviewed: 2026-07-01
        ---

        # Glossary

        {{body}}
        """;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class GlossaryRepository : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public GlossaryRepository(IReadOnlyList<string> docsRoots, IReadOnlyList<string> catalogRows)
        {
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoots(docsRoots);
            foreach (var root in docsRoots) Directory.CreateDirectory(FullPath(root));
            Write(Ontology.ResolvedCatalogPath, $$"""
                ---
                id: system/catalog
                title: Catalog
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-08-01
                ---

                # Catalog

                | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
                | --- | --- | --- | --- | --- | --- | --- | --- |
                {{string.Join('\n', catalogRows)}}
                """);
        }

        public string Root => _temp.Path;
        public OntologyConfig Ontology { get; }

        public string FullPath(string relativePath) =>
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public GlossaryRepository WriteGlossary(string markdown)
        {
            Write("docs/glossary.md", markdown);
            return this;
        }

        public void ReplaceInGlossary(string oldValue, string newValue)
        {
            var path = FullPath("docs/glossary.md");
            File.WriteAllText(path, File.ReadAllText(path).Replace(oldValue, newValue, StringComparison.Ordinal));
        }

        public void WriteConfig(string yaml) => Write(".kyber-weave/kyber-weave.yml", yaml);

        private void Write(string relativePath, string content)
        {
            var path = FullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => _temp.Dispose();
    }
}
