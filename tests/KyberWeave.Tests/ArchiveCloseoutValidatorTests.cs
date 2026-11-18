using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Shared corpus builders for the archive-closeout RED suite. Every passing shape is
/// built from <see cref="PlanDocument"/> with its defaults: a superseded plan whose
/// Decisions, Closeout mapping and Design harvest tables agree, whose impact names a
/// catalog component carried fresh by the mapped document. Each condition test mutates
/// exactly one aspect of that shape so the failure it prevents stays identifiable.
/// </summary>
/// <summary>
/// The closeout gate's conditions: what archived work must carry before
/// <c>KW-DOC-LIFECYCLE-004</c> stays silent. Every test here fails while the validator
/// is a skeleton that returns an empty report, and passes only when the gate reports
/// the named condition for the archived entry.
/// </summary>
public sealed class ArchiveCloseoutValidatorTests
{
    private const string PlanPath = "6-Docs/archive/plans/2026-10-20-widget.md";

    private const string TargetPath = "6-Docs/reference/widget.md";

    private static DiagnosticReport Validate(
        DocFixture fixture,
        KyberWeaveConfig config,
        params ChangedPath[] changes)
    {
        return new ArchiveCloseoutValidator(
            fixture.Root,
            config,
            () => RealDocumentLoader.Create(fixture.Root, config.Ontology),
            DocsPorts.Files, DocsPorts.Paths, DocsPorts.Environment).Validate(
            RealDocumentLoader.Create(fixture.Root, config.Ontology).Load(), changes);
    }

    private static string FindingText(Diagnostic finding)
    {
        return string.Join(" ", [finding.Message, finding.Hint ?? string.Empty]);
    }

    private static DocFixture WithTarget(DocFixture fixture)
    {
        return WithTarget(fixture, CloseoutCorpus.TargetDocument());
    }

    private static DocFixture WithTarget(DocFixture fixture, string target)
    {
        return fixture.WithCatalog().Write(TargetPath, target);
    }

    /// <summary>A draft in the archive is not closed work, so its status fails C1.</summary>
    [Fact]
    public void DraftFrontmatterStatusFailsCloseoutAsC1()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(status: "draft"));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C1", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A plan archived with a task it never delivered is not closed work, however complete its harvest is. The
    /// second end-to-end tiered run archived a plan whose task 1.2 was never implemented, and the build that ran the
    /// archive tool carried no checkbox gate at all; this is the check the merge gate can make of the archived copy
    /// whatever tool moved it.
    /// </summary>
    [Fact]
    public void AnArchivedPlanWithAnUncheckedNumberedTaskIsReported()
    {
        string sections = CloseoutCorpus.DecisionsSection("D1")
            + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../../reference/widget.md)"))
            + CloseoutCorpus.DesignHarvestSection("H1")
            + """

              ## Tasks

              - [x] 1.1 Slug rule
              - [ ] 1.2 Word count

              """;
        using DocFixture fixture = WithTarget(new DocFixture())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections: sections));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("1.2", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>A prose checklist and a fenced example are not tasks, so a closed plan carrying them still validates.</summary>
    [Fact]
    public void AProseChecklistAndAFencedExampleAreNotUncheckedTasks()
    {
        string sections = CloseoutCorpus.DecisionsSection("D1")
            + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../../reference/widget.md)"))
            + CloseoutCorpus.DesignHarvestSection("H1")
            + """

              ## Tasks

              - [x] 1.1 Slug rule

              ## Open questions

              - [ ] confirm rollout with the owner

              ```text
              - [ ] 9.9 an example of the checkbox grammar
              ```

              """;
        using DocFixture fixture = WithTarget(new DocFixture())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections: sections));

        DiagnosticReport report = Validate(
            fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added));

        Assert.DoesNotContain(report.Items, item => item.Code == ArchiveCloseoutValidator.MissingCloseoutEvidence);
    }

    /// <summary>
    /// The inventory a kind keeps in its archive folder lists archived entries and is not one,
    /// so it carries no closeout evidence to check.
    /// </summary>
    [Fact]
    public void TheArchiveInventoryIsNotAnArchivedEntry()
    {
        const string inventory = "6-Docs/archive/plans/README.md";
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(
                inventory,
                "---\nid: archive/plans/index\ntitle: Archived plan inventory\ndoc-type: index\n"
                + "status: current\nowner: dpalfery\nlast-reviewed: 2026-11-09\n---\n\n# Archived plan inventory\n");

        DiagnosticReport report = Validate(
            fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(inventory, ChangeKind.Added));

        Assert.Empty(report.Items);
    }

    /// <summary>A specification folder's own README one level deeper is still an archived entry.</summary>
    [Fact]
    public void AnArchivedSpecificationFolderReadmeIsStillAnEntry()
    {
        const string specReadme = "6-Docs/archive/specs/widget/README.md";
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(specReadme, CloseoutCorpus.PlanDocument(status: "draft"));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(specReadme, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
    }

    /// <summary>
    /// Only the plans kind keeps an inventory in its archive folder. A README.md at that depth in
    /// another kind is an archived entry like any other and must still carry closeout evidence.
    /// </summary>
    [Theory]
    [InlineData("6-Docs/archive/specs/README.md")]
    [InlineData("6-Docs/archive/todo/README.md")]
    [InlineData("6-Docs/archive/adr/README.md")]
    public void AnotherKindsTopLevelArchiveReadmeIsStillAnEntry(string readme)
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(readme, CloseoutCorpus.PlanDocument(status: "draft"));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(readme, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(readme, finding.FilePath);
    }

    /// <summary>Without a Decisions table there is nothing to harvest, which fails C3.</summary>
    [Fact]
    public void MissingDecisionsSectionFailsCloseoutAsC3()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../../reference/widget.md)"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C3", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A Review section that forecasts a closeout is not a Closeout mapping table, so C4 fails.
    /// </summary>
    [Fact]
    public void ReviewForecastWithoutCloseoutMappingFailsCloseoutAsC4()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.ReviewForecastSection
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C4", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>An archived plan that declares no authoring scale fails C5.</summary>
    [Fact]
    public void MissingScaleFailsCloseoutAsC5()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(impactLine: string.Empty, componentsLine: string.Empty));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C5", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>A scale outside new-component, extends-component and no-design-change fails C5.</summary>
    [Fact]
    public void UnknownScaleFailsCloseoutAsC5()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(impactLine: "documentation-impact: quantum-entanglement"));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C5", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>A component scale with no impacted components named fails C5.</summary>
    [Fact]
    public void ScaleWithoutComponentsFailsCloseoutAsC5()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(componentsLine: "impact-components: []"));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C5", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// no-design-change is a claim that nothing survives, so without a Documentation waiver
    /// reason in the Design harvest section it fails C5.
    /// </summary>
    [Fact]
    public void NoDesignChangeWithoutAReasonFailsCloseoutAsC5()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(
                impactLine: "documentation-impact: no-design-change",
                componentsLine: string.Empty,
                sections: CloseoutCorpus.DecisionsSection("D1")
                          + CloseoutCorpus.CloseoutMappingSection(("D1",
                              "[Widget architecture](../../reference/widget.md)"))
                          + CloseoutCorpus.WaiverlessDesignSection));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C5", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>A component scale with no Design harvest table fails C6.</summary>
    [Fact]
    public void MissingDesignHarvestFailsCloseoutAsC6()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1",
                    "[Widget architecture](../../reference/widget.md)"))));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C6", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>Every decision id must be mapped, so the finding names the one that is not.</summary>
    [Fact]
    public void UnmappedDecisionIdIsNamedInTheFinding()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1", "D2")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../../reference/widget.md)"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C7", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("D2", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>Every Design harvest id must be mapped too, so the finding names the stale one.</summary>
    [Fact]
    public void UnmappedHarvestIdIsNamedInTheFinding()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../../reference/widget.md)"))
                + CloseoutCorpus.DesignHarvestSection("H1", "H2")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C7", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("H2", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>An ADR waiver excuses the lasting record, not the harvest mapping, so C7 fails.</summary>
    [Fact]
    public void AdrWaiverAloneMapsNothing()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "ADR waiver: refines an established pattern."))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C7", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("D1", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A Documentation waiver maps the decision it excuses. The control entry still fails,
    /// so the single finding for the control proves the waiver entry passed.
    /// </summary>
    [Fact]
    public void DocumentationWaiverMapsTheDecision()
    {
        const string WaiverPath = "6-Docs/archive/plans/2026-10-22-waiver.md";
        const string ControlPath = "6-Docs/archive/plans/2026-10-21-control.md";
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(ControlPath, CloseoutCorpus.ControlPlan())
            .Write(WaiverPath, CloseoutCorpus.PlanDocument(
                "plans/2026-10-22-waiver",
                sections: CloseoutCorpus.DecisionsSection("D1")
                          + CloseoutCorpus.CloseoutMappingSection(("D1, H1",
                              "Documentation waiver: transient spike with no surviving invariants."))
                          + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(Validate(
            fixture,
            KyberWeaveConfigDefaults.Product,
            new ChangedPath(ControlPath, ChangeKind.Added),
            new ChangedPath(WaiverPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(ControlPath, finding.FilePath);
    }

    /// <summary>A link to code is not a governed document, so the decision stays unmapped.</summary>
    [Fact]
    public void LinkToCodeDoesNotMapTheDecision()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write("src/Widget.cs", "// Implementation, not a governed document.\n")
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget implementation](../../src/Widget.cs)"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C7", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("D1", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>A link to a document that does not exist maps nothing.</summary>
    [Fact]
    public void LinkToAMissingDocumentDoesNotMapTheDecision()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Ghost widget](../../reference/ghost.md)"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C7", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("D1", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>An archived link target is closed work, not a live governed document.</summary>
    [Fact]
    public void LinkToAnArchivedDocumentDoesNotMapTheDecision()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write("6-Docs/archive/reference/old.md", CloseoutCorpus.TargetDocument("reference/old"))
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Old widget](../../archive/reference/old.md)"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C7", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("D1", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A bold-prefixed waiver is still a leading waiver: markdown emphasis around the
    /// marker does not demote it to prose, so the decision maps.
    /// </summary>
    [Fact]
    public void BoldPrefixedWaiverStillMapsTheDecision()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1",
                    "**Documentation waiver — transient spike:** no surviving invariants."))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Assert.Empty(Validate(
            fixture,
            KyberWeaveConfigDefaults.Product,
            new ChangedPath(PlanPath, ChangeKind.Added)).Items);
    }

    /// <summary>
    /// A waiver marker buried after an unresolvable link is not a waiver: the row maps
    /// nothing, so the decision stays unmapped (C7).
    /// </summary>
    [Fact]
    public void BuriedWaiverMarkerAfterUnresolvableLinkMapsNothing()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1",
                    "[Ghost widget](../../reference/ghost.md) documentation waiver noted"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C7", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("D1", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A pipe table inside a fenced code block is an example, not evidence: with no
    /// real Decisions table the entry fails C3.
    /// </summary>
    [Fact]
    public void FencedExampleTableIsNotADecisionsTable()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                "## Decisions\n\nAn example of the table shape:\n\n```\n| ID | Question |\n| --- | --- |\n| D1 | Example? |\n```\n"
                + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../../reference/widget.md)"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C3", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A comma-separated first cell maps each id it names. The control entry still fails,
    /// so the single finding for the control proves the comma list passed.
    /// </summary>
    [Fact]
    public void CommaSeparatedIdsMapEachDecision()
    {
        const string ControlPath = "6-Docs/archive/plans/2026-10-21-control.md";
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(ControlPath, CloseoutCorpus.ControlPlan())
            .Write(PlanPath, CloseoutCorpus.PlanDocument());

        Diagnostic finding = Assert.Single(Validate(
            fixture,
            KyberWeaveConfigDefaults.Product,
            new ChangedPath(ControlPath, ChangeKind.Added),
            new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(ControlPath, finding.FilePath);
    }

    /// <summary>A hyphenated range is not expanded, so the ids inside it stay unmapped.</summary>
    [Fact]
    public void AHyphenatedRangeDoesNotExpand()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1", "D2")
                + CloseoutCorpus.CloseoutMappingSection(
                    ("D1-D2", "[Widget architecture](../../reference/widget.md)"),
                    ("H1", "[Widget architecture](../../reference/widget.md)"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C7", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("D2", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>RESIDUAL on its own maps nothing and tracks nothing, failing C7 and C9.</summary>
    [Fact]
    public void ResidualWithoutATodoLinkMapsNothing()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
                CloseoutCorpus.DecisionsSection("D1")
                + CloseoutCorpus.CloseoutMappingSection(
                    ("D1", "[Widget architecture](../../reference/widget.md)"),
                    ("H1", "RESIDUAL"))
                + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C9", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("RESIDUAL", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A RESIDUAL line that links an existing document inside the todo-index folder is
    /// tracked. The control entry still fails, so the single finding for the control
    /// proves the RESIDUAL entry passed.
    /// </summary>
    [Fact]
    public void ResidualWithAnExistingTodoPasses()
    {
        const string ResidualPath = "6-Docs/archive/plans/2026-10-23-residual.md";
        const string ControlPath = "6-Docs/archive/plans/2026-10-21-control.md";
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write("6-Docs/todo/cleanup.md", CloseoutCorpus.TodoDocument())
            .Write(ControlPath, CloseoutCorpus.ControlPlan())
            .Write(ResidualPath, CloseoutCorpus.PlanDocument(
                "plans/2026-10-23-residual",
                sections: CloseoutCorpus.DecisionsSection("D1")
                          + CloseoutCorpus.CloseoutMappingSection(
                              ("D1", "[Widget architecture](../../reference/widget.md)"),
                              ("H1", "RESIDUAL, tracked in [Cleanup](../../todo/cleanup.md)"))
                          + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(Validate(
            fixture,
            KyberWeaveConfigDefaults.Product,
            new ChangedPath(ControlPath, ChangeKind.Added),
            new ChangedPath(ResidualPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(ControlPath, finding.FilePath);
    }

    /// <summary>An impact component outside the catalog vocabulary fails C8.</summary>
    [Fact]
    public void ImpactComponentOutsideTheCatalogFailsCloseoutAsC8()
    {
        using DocFixture fixture = WithTarget(DocFixture.Create())
            .Write(PlanPath, CloseoutCorpus.PlanDocument(
                componentsLine: "impact-components:\n  - MotorcycleRAG Imaginary"));

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C8", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// The mapped document must carry the impact component: a catalog row that is some
    /// other component fails C8.
    /// </summary>
    [Fact]
    public void MappedDocumentWithoutTheComponentFailsCloseoutAsC8()
    {
        using DocFixture fixture = WithTarget(
                DocFixture.Create(),
                CloseoutCorpus.TargetDocument(component: "MotorcycleRAG system"))
            .Write(PlanPath, CloseoutCorpus.PlanDocument());

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C8", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// Currency is evidence: a mapped document reviewed before the harvest document fails C8.
    /// </summary>
    [Fact]
    public void StaleMappedDocumentFailsCloseoutAsC8()
    {
        using DocFixture fixture = WithTarget(
                DocFixture.Create(),
                CloseoutCorpus.TargetDocument(lastReviewed: "2026-01-01"))
            .Write(PlanPath, CloseoutCorpus.PlanDocument());

        Diagnostic finding = Assert.Single(
            Validate(fixture, KyberWeaveConfigDefaults.Product, new ChangedPath(PlanPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C8", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// One fresh mapped document carrying the component covers C8 even beside stale ones.
    /// The control entry still fails, so the single finding for the control proves it.
    /// </summary>
    [Fact]
    public void OneFreshMappedDocumentAmongStaleOnesPasses()
    {
        const string MixedPath = "6-Docs/archive/plans/2026-10-24-mixed.md";
        const string ControlPath = "6-Docs/archive/plans/2026-10-21-control.md";
        using DocFixture fixture = WithTarget(
                DocFixture.Create(),
                CloseoutCorpus.TargetDocument(lastReviewed: "2026-01-01"))
            .Write("6-Docs/reference/widget-v2.md", CloseoutCorpus.TargetDocument("reference/widget-v2"))
            .Write(ControlPath, CloseoutCorpus.ControlPlan())
            .Write(MixedPath, CloseoutCorpus.PlanDocument(
                "plans/2026-10-24-mixed",
                sections: CloseoutCorpus.DecisionsSection("D1")
                          + CloseoutCorpus.CloseoutMappingSection(
                              ("D1", "[Widget architecture](../../reference/widget.md)"),
                              ("H1", "[Widget v2 architecture](../../reference/widget-v2.md)"))
                          + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(Validate(
            fixture,
            KyberWeaveConfigDefaults.Product,
            new ChangedPath(ControlPath, ChangeKind.Added),
            new ChangedPath(MixedPath, ChangeKind.Added)).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(ControlPath, finding.FilePath);
    }
}
