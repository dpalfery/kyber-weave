using KyberWeave.Core.Docs.Search;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T22 — index sections are navigation: a matching row answers from the target document,
/// not from the inventory table body.
/// </summary>
public sealed class IndexSectionExploreTests
{
    private const string TargetMarker = "ALPINE-HARVEST-SCOPE-MARKER-XYZZY";
    private const string InventoryNoise = "INVENTORY-TABLE-NOISE-PIPE-ROW-ABCDEF";

    [Fact]
    public void AQueryMatchingAnArchivedPlanRowAnswersFromTheTargetPlanNotTheInventoryTable()
    {
        string indexBody = """
                           ---
                           id: plans/index
                           title: Plan inventory fixture
                           doc-type: index
                           status: current
                           owner: Maintainers
                           last-reviewed: 2026-10-31
                           ---

                           # Plan inventory

                           ## Archived Plans

                           | Plan | Status | Goal |
                           |---|---|---|
                           | [Alpine telemetry harvest](plans/alpine-plan.md) | Archived | INVENTORY-TABLE-NOISE-PIPE-ROW-ABCDEF |
                           """;

        string targetBody = """
                            ---
                            id: plans/alpine-plan
                            title: Alpine telemetry harvest
                            doc-type: plan
                            status: current
                            owner: Maintainers
                            last-reviewed: 2026-10-31
                            ---

                            # Alpine telemetry harvest

                            ## Objective

                            ALPINE-HARVEST-SCOPE-MARKER-XYZZY
                            """;

        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/plans/README.md", indexBody)
            .Write("6-Docs/plans/alpine-plan.md", targetBody);

        DocumentIndex index = DocumentIndex.Build(fixture.Load(), FakeCodeGraphResolver.WithSymbols());
        ExploreAnswer answer = index.Explore("Alpine telemetry harvest");

        Assert.False(answer.IsMiss);
        Assert.Equal("6-Docs/plans/alpine-plan.md", answer.SourceDocument?.RelativePath);
        Assert.Contains(TargetMarker, answer.Prose, StringComparison.Ordinal);
        Assert.DoesNotContain(InventoryNoise, answer.Prose, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchingIndexRowDoesNotAnswerFromATargetBelowTheDocumentFloor()
    {
        const string targetMarker = "UNRELATED-COPPER-WIDGET-MARKER-XYZZY";
        string indexBody = """
                           ---
                           id: plans/index
                           title: Plan inventory fixture
                           doc-type: index
                           status: current
                           owner: Maintainers
                           last-reviewed: 2026-10-31
                           ---

                           # Plan inventory

                           ## Archived Plans

                           | Plan | Status | Goal |
                           |---|---|---|
                           | [Alpine telemetry harvest](unrelated.md) | Archived | stored elsewhere |
                           """;

        string targetBody = """
                            ---
                            id: plans/unrelated
                            title: Copper widget inventory
                            doc-type: plan
                            status: current
                            owner: Maintainers
                            last-reviewed: 2026-10-31
                            ---

                            # Copper widget inventory

                            ## Objective

                            UNRELATED-COPPER-WIDGET-MARKER-XYZZY
                            """;

        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/plans/README.md", indexBody)
            .Write("6-Docs/plans/unrelated.md", targetBody);

        DocumentIndex index = DocumentIndex.Build(fixture.Load(), FakeCodeGraphResolver.WithSymbols());
        ExploreAnswer answer = index.Explore("Alpine telemetry harvest");

        Assert.False(answer.IsMiss);
        Assert.Equal("6-Docs/plans/README.md", answer.SourceDocument?.RelativePath);
        Assert.DoesNotContain(targetMarker, answer.Prose, StringComparison.Ordinal);
    }
}
