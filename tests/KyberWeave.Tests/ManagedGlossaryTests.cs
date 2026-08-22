using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Model;
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
    public void PreviewDefaultOrConfiguredPathUsesConfiguredCorpusLocationWithoutWriting(
        string? configuredPath,
        string expectedPath)
    {
        using GlossaryRepository repository = Repository(docsRoots: ["docs", "components/gameplay/docs"]);
        ManagedGlossaryService service = Service(repository, glossaryPath: configuredPath);

        GlossaryUpdateResult result = service.Preview([Proposal("loop", "component:Gameplay", "claim-1")]);

        Assert.Equal(expectedPath, result.RelativePath);
        Assert.False(result.Written);
        Assert.False(File.Exists(repository.FullPath(expectedPath)));
        Assert.Contains("## loop", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteMissingGlossaryCreatesConformantReferenceUsingFirstCatalogOwnerAndUtcDate()
    {
        using GlossaryRepository repository = Repository();
        ManagedGlossaryService service = Service(repository);

        GlossaryUpdateResult result = service.Write([Proposal("loop", "component:Gameplay", "claim-1")]);

        Assert.True(result.Written);
        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("doc-type: reference", markdown, StringComparison.Ordinal);
        Assert.Contains("status: needs-review", markdown, StringComparison.Ordinal);
        Assert.Contains("owner: Gameplay maintainers", markdown, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: 2026-08-11", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteCatalogHasNoDataOwnerFailsBeforeCreatingGlossary()
    {
        using GlossaryRepository repository = Repository(catalogRows: []);
        ManagedGlossaryService service = Service(repository);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            service.Write([Proposal("loop", "component:Gameplay", "claim-1")]));

        Assert.Contains("owner", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(repository.FullPath("docs/glossary.md")));
    }

    [Fact]
    public void WriteNewProposalUsesExactTableShapeStatusScopeAndMarkedEvidence()
    {
        using GlossaryRepository repository = Repository();
        ManagedGlossaryService service = Service(repository);

        service.Write([Proposal("loop", "component:Gameplay", "claim-1", aliases: ["gameplay loop"])]);

        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("| Sense ID | Status | Definition | Scope | Aliases |", markdown, StringComparison.Ordinal);
        Assert.Matches(@"\| loop-[a-f0-9]{8} \| proposed \|  \| component:Gameplay \| gameplay loop \|", markdown);
        Assert.Contains("<!-- kyber-weave:glossary-evidence:start", markdown, StringComparison.Ordinal);
        Assert.Contains("claim-1", markdown, StringComparison.Ordinal);
        Assert.Contains("<!-- kyber-weave:glossary-evidence:end", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteExistingGlossaryPreservesOwnerDateHumanProseAndReviewedRows()
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary(
            """
            Human introduction that must stay intact.

            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-approved1 | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |
            | loop-rejected1 | rejected | Not a useful meaning. | component:Agents | churn loop |

            Human notes that must also stay intact.
            """));
        ManagedGlossaryService service = Service(repository);

        service.Write([Proposal("loop", "component:Runtime", "claim-2")]);

        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("owner: Human owner", markdown, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: 2026-07-01", markdown, StringComparison.Ordinal);
        Assert.Contains("Human introduction that must stay intact.", markdown, StringComparison.Ordinal);
        Assert.Contains("Human notes that must also stay intact.", markdown, StringComparison.Ordinal);
        Assert.Contains("| loop-approved1 | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |", markdown, StringComparison.Ordinal);
        Assert.Contains("| loop-rejected1 | rejected | Not a useful meaning. | component:Agents | churn loop |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteProposalEvidenceChangesRefreshesEvidenceAndDemotesCurrentDocument()
    {
        using GlossaryRepository repository = Repository();
        ManagedGlossaryService service = Service(repository);
        service.Write([Proposal("loop", "component:Gameplay", "old-claim", aliases: ["gameplay loop"])]);
        repository.ReplaceInGlossary("status: needs-review", "status: current");
        repository.ReplaceInGlossary("last-reviewed: 2026-08-11", "last-reviewed: 2026-07-01");

        GlossaryUpdateResult result = service.Write([Proposal("loop", "component:Gameplay", "new-claim", aliases: ["gameplay loop"])]);

        Assert.True(result.Changed);
        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("status: needs-review", markdown, StringComparison.Ordinal);
        Assert.Contains("new-claim", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("old-claim", markdown, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: 2026-07-01", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteUntouchedGeneratedProposalLosesAllEvidenceRemovesProposal()
    {
        using GlossaryRepository repository = Repository();
        ManagedGlossaryService service = Service(repository);
        service.Write([Proposal("loop", "component:Gameplay", "old-claim", aliases: ["gameplay loop"])]);

        service.Write([]);

        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.DoesNotContain("| proposed |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("old-claim", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteProposalWithoutOwnershipFingerprintLosesEvidencePreservesRow()
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary(
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

        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("loop-a1b2c3d4", markdown, StringComparison.Ordinal);
        Assert.Contains("old-claim", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteGeneratedProposalEvidenceWasHumanEditedPreservesRowAndEvidence()
    {
        using GlossaryRepository repository = Repository();
        ManagedGlossaryService service = Service(repository);
        service.Write([Proposal("loop", "component:Gameplay", "claim-1", aliases: ["gameplay loop"])]);
        repository.ReplaceInGlossary("- claim-1", "- human evidence note");

        service.Write([]);

        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("| proposed |", markdown, StringComparison.Ordinal);
        Assert.Contains("human evidence note", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteFencedManagedHeadingAndTableExamplePreservesExampleByteForByte()
    {
        const string example = """
            ```markdown
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | example-loop | approved | Example only. | component:Gameplay | example |
            ```
            """;
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary(example));

        Service(repository).Write([Proposal("loop", "component:Agents", "claim-1")]);

        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains(example, markdown, StringComparison.Ordinal);
        Assert.True(markdown.LastIndexOf("## loop", StringComparison.Ordinal)
                    > markdown.IndexOf("\n```\n", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRealTermWithFencedManagedTableAndEvidenceIgnoresFencedLookalikes()
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
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            {{fencedExample}}

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |
            """));

        DiagnosticReport report = Service(repository).Validate();

        Assert.Empty(report.Items);
    }

    [Fact]
    public void PreviewAndWriteRealTermWithFencedManagedTableAndEvidencePreserveFencedBytes()
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
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            {{fencedExample}}

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |
            """));
        ManagedGlossaryService service = Service(repository);
        GlossaryProposal[] proposals = [Proposal("loop", "component:Agents", "claim-1")];

        GlossaryUpdateResult preview = service.Preview(proposals);
        GlossaryUpdateResult written = service.Write(proposals);

        Assert.Contains(fencedExample, preview.Markdown, StringComparison.Ordinal);
        Assert.Contains(fencedExample, written.Markdown, StringComparison.Ordinal);
        Assert.Contains(fencedExample, File.ReadAllText(repository.FullPath("docs/glossary.md")), StringComparison.Ordinal);
        Assert.True(written.Markdown.LastIndexOf("| proposed |", StringComparison.Ordinal)
                    > written.Markdown.LastIndexOf("```", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Human-authored definition", "component:Gameplay", "game cycle")]
    [InlineData("", "component:Gameplay; code-ref:Runner.Loop", "human alias")]
    public void WriteEditedProposalLosesEvidencePreservesHumanEdits(
        string definition,
        string scope,
        string aliases)
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a1b2c3d4 | proposed | {{definition}} | {{scope}} | {{aliases}} |

            <!-- kyber-weave:glossary-evidence:start sense="loop-a1b2c3d4" -->
            - old-claim
            <!-- kyber-weave:glossary-evidence:end -->
            """));
        ManagedGlossaryService service = Service(repository);

        service.Write([]);

        string markdown = File.ReadAllText(repository.FullPath("docs/glossary.md"));
        Assert.Contains("loop-a1b2c3d4", markdown, StringComparison.Ordinal);
        if (definition.Length > 0)
            Assert.Contains(definition, markdown, StringComparison.Ordinal);
        Assert.Contains(scope, markdown, StringComparison.Ordinal);
        Assert.Contains(aliases, markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("pending")]
    [InlineData("CURRENT")]
    public void ValidateUnknownSenseStatusReturnsGlossaryDiagnostic(string status)
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a1b2c3d4 | {{status}} | A definition. | component:Gameplay | gameplay loop |
            """));

        DiagnosticReport report = Service(repository).Validate();

        Assert.Contains(report.Items, item => item.Code == ManagedGlossaryService.ValidationRuleCode);
        Assert.All(report.Items, item => Assert.Equal("KW-DOC-GLOSSARY-001", item.Code));
    }

    [Theory]
    [InlineData("", "component:Gameplay")]
    [InlineData("A definition.", "")]
    [InlineData("A definition.", "team:Gameplay")]
    [InlineData("A definition.", "component:Unknown")]
    public void ValidateInvalidApprovedSenseReturnsGlossaryDiagnostic(string definition, string scope)
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary($$"""
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a1b2c3d4 | approved | {{definition}} | {{scope}} | gameplay loop |
            """));

        DiagnosticReport report = Service(repository).Validate();

        Assert.Contains(report.Items, item => item.Code == "KW-DOC-GLOSSARY-001");
    }

    [Fact]
    public void LoadApprovedAndRejectedRowsReturnsOnlyApprovedAnalysisSenses()
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary(
            """
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay | gameplay loop |
            | loop-agent | rejected | Rejected meaning. | component:Agents | churn loop |
            """));

        AnalysisGlossary glossary = Service(repository).Load().AnalysisGlossary;

        ApprovedGlossarySense sense = Assert.Single(glossary.Senses);
        Assert.Equal("loop-gameplay", sense.Id);
        Assert.Equal("The gameplay update cycle.", sense.Definition);
        Assert.Equal(["component:Gameplay"], sense.Scopes);
        Assert.Equal(["gameplay loop"], sense.Aliases);
    }

    [Fact]
    public void LookupTermIsCaseInsensitiveReturnsAllStatusesAndParsedScopes()
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary(
            """
            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay; code-ref:Game.Run | gameplay loop |
            | loop-agent | proposed |  | component:Agents | churn loop |
            """));

        GlossaryLookupResult result = Service(repository).Lookup("LOOP");

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
    public void ValidateMalformedFrontmatterOrSenseRowFailsClosed(string markdown)
    {
        using GlossaryRepository repository = Repository().WriteGlossary(markdown);

        DiagnosticReport report = Service(repository).Validate();

        Assert.Contains(report.Items, item => item.Code == "KW-DOC-GLOSSARY-001");
    }

    [Fact]
    public void LoadIndentedAtxHeadingUsesTermWithoutHashPrefix()
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary("""
              ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a | approved | A definition. | component:Gameplay | gameplay loop |
            """));

        ManagedGlossaryLoadResult result = Service(repository).Load();

        GlossaryLookupResult term = Assert.Single(result.Terms);
        Assert.Equal("loop", term.Term);
        Assert.DoesNotContain("# loop", result.Terms.Select(item => item.Term));
    }

    [Fact]
    public void LoadDefinitionWithBackslashesPreservesWindowsPathAndRegexEscapes()
    {
        using GlossaryRepository repository = Repository().WriteGlossary(ExistingGlossary("""
            ## path

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | path-a | approved | Output is C:\build\out and tokens match \w+. | component:Gameplay | output path |
            """));

        ManagedGlossaryLoadResult result = Service(repository).Load();

        GlossarySense sense = Assert.Single(Assert.Single(result.Terms).Senses);
        Assert.Equal(@"Output is C:\build\out and tokens match \w+.", sense.Definition);
    }

    [Fact]
    public void WriteFirstCatalogOwnerContainsYamlPunctuationRoundTripsExactOwner()
    {
        const string owner = "Docs: Core # on-call";
        using GlossaryRepository repository = Repository(catalogRows:
        [
            $"| Gameplay | Application | `src/Game` | [README](x) | [docs](y) | {owner} | 2026-08-01 | Current |"
        ]);

        Service(repository).Write([Proposal("loop", "component:Gameplay", "claim-1")]);

        DocumentSet set = new DocumentLoader(repository.Root, repository.Ontology).Load();
        DocumentModel glossary = Assert.Single(set.Documents, document => document.RelativePath == "docs/glossary.md");
        Assert.Null(glossary.ParseError);
        Assert.Equal(owner, glossary.Frontmatter.Owner);
    }

    [Fact]
    public void DocsValidateInvalidManagedGlossaryReturnsGlossaryOperationalError()
    {
        using GlossaryRepository repository = Repository(catalogRows:
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
        DocsSettings settings = new DocsSettings { Path = repository.Root, Format = "json" };

        int exitCode = ProcessConsoleCapture.Run(() => new DocsValidateCommand().Execute(null!, settings)).Result;

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
            foreach (string root in docsRoots) Directory.CreateDirectory(FullPath(root));
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
            string path = FullPath("docs/glossary.md");
            File.WriteAllText(path, File.ReadAllText(path).Replace(oldValue, newValue, StringComparison.Ordinal));
        }

        public void WriteConfig(string yaml) => Write(".kyber-weave/kyber-weave.yml", yaml);

        private void Write(string relativePath, string content)
        {
            string path = FullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => _temp.Dispose();
    }
}
