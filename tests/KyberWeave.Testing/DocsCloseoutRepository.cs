namespace KyberWeave.Tests;

/// <summary>
/// The repository the <c>docs closeout</c> tests run against: one plan ready to archive, its task file, a target
/// document, a plan index with one row and an archive inventory with one row. The same tree is built in memory for the
/// unit tests and on disk for the tests whose subject is the disk.
/// </summary>
internal static class DocsCloseoutRepository
{
    internal const string Plan = "6-Docs/plans/2026-10-20-widget.md";
    internal const string TaskFile = "6-Docs/plans/2026-10-20-widget.tasks.yml";
    internal const string Archived = "6-Docs/archive/plans/2026-10-20-widget.md";
    internal const string PlanIndex = "6-Docs/plans/README.md";
    internal const string Inventory = "6-Docs/archive/plans/README.md";
    internal const string Date = "2026-11-13";

    /// <summary>Builds the repository.</summary>
    /// <param name="inMemory">True for the in-memory file system, false for a real directory.</param>
    /// <param name="planText">The plan's text; a plan ready to archive when null.</param>
    /// <param name="planIndex">The plan index's text; an index with the plan's row when null.</param>
    internal static DocFixture Create(bool inMemory, string? planText = null, string? planIndex = null)
    {
        DocFixture fixture = (inMemory ? DocFixture.CreateInMemory() : DocFixture.Create()).WithCatalog()
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(Plan, planText ?? CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", linkPrefix: ".."))
            .Write(TaskFile, "{}\n")
            .Write(PlanIndex, planIndex ?? PlanIndexWithRow)
            .Write(Inventory, InventoryWithOneRow);
        if (inMemory)
        {
            fixture.Memory!.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        }

        return fixture;
    }

    internal static string PlanIndexWithRow => """
        ---
        id: plans/index
        title: Plan Inventory
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-11-01
        ---

        # Plan Inventory

        ## Active Plans

        | Plan | Status |
        |---|---|
        | [2026-10-20-widget.md](2026-10-20-widget.md) | Ready |

        ## Archived Plans

        See the archive.
        """;

    internal static string InventoryWithOneRow => """
        ---
        id: archive/plans/index
        title: Archived Plan Inventory
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-11-01
        ---

        # Archived Plan Inventory

        | Plan | Status | Date | Archive Date | Canonical Docs / Harvested ADRs | Goal |
        |---|---|---|---|---|---|
        | [older.md](older.md) | Archived — complete 2026-10-01 | 2026-10-01 | 2026-10-01 | none | Older plan |
        """;
}
