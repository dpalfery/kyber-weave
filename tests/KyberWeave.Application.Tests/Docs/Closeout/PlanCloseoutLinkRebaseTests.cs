using System.Globalization;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A plan's links are written for the plans folder, and archiving moves it one folder deeper. The archived plan and
/// the inventory row copied from its Closeout mapping must both point at the file the link named before the move.
/// </summary>
public sealed class PlanCloseoutLinkRebaseTests
{
    private const string Plan = DocsCloseoutRepository.Plan;
    private const string Archived = DocsCloseoutRepository.Archived;
    private const string Inventory = DocsCloseoutRepository.Inventory;

    private static DateOnly ArchiveDate => DateOnly.Parse(DocsCloseoutRepository.Date, CultureInfo.InvariantCulture);

    [Fact]
    public void ArchivingRewritesThePlanLinkAndTheInventoryRowForTheArchiveFolder()
    {
        string sections = CloseoutCorpus.DecisionsSection("D1")
                          + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Architecture](../admin-dashboard/architecture.md#install)"))
                          + CloseoutCorpus.DesignHarvestSection("H1");
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/admin-dashboard/architecture.md", CloseoutCorpus.TargetDocument("admin-dashboard/architecture"))
            .Write(Plan, CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", sections: sections, linkPrefix: ".."))
            .Write(DocsCloseoutRepository.TaskFile, "{}\n")
            .Write(DocsCloseoutRepository.PlanIndex, DocsCloseoutRepository.PlanIndexWithRow)
            .Write(Inventory, DocsCloseoutRepository.InventoryWithOneRow);

        PlanCloseoutOutcome outcome = new PlanCloseout(
                fixture.Root,
                KyberWeaveConfigDefaults.Product,
                () => fixture.Loader(KyberWeaveConfigDefaults.Product.Ontology),
                fixture.Store,
                new ScriptedProcessExecutor(_ => new ProcessResult(128, string.Empty, "fatal: not a git repository")),
                new InMemoryExclusiveFileWriter(fixture.Memory!),
                fixture.Paths,
                fixture.Environment)
            .Run(Plan, ArchiveDate, archive: true);

        Assert.True(outcome.Archived);
        Assert.Contains(
            "[Architecture](../../admin-dashboard/architecture.md#install)",
            fixture.ReadText(Archived),
            StringComparison.Ordinal);
        string row = fixture.ReadText(Inventory).Split('\n')
            .First(line => line.StartsWith("| [2026-10-20-widget.md]", StringComparison.Ordinal));
        Assert.Contains(
            "[Architecture](../../admin-dashboard/architecture.md#install) (D1, H1)",
            row,
            StringComparison.Ordinal);
    }
}
