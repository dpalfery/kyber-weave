namespace KyberWeave.Tests;

internal static class CloseoutCorpus
{
    public const string Component = "MotorcycleRAG API";

    public const string HarvestLastReviewed = "2026-10-20";

    public const string Catalog = """
                                  ---
                                  id: system/catalog
                                  title: Component Catalog
                                  doc-type: index
                                  status: current
                                  owner: Maintainers
                                  last-reviewed: 2026-07-21
                                  ---

                                  # Catalog

                                  | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
                                  | --- | --- | --- | --- | --- | --- | --- | --- |
                                  | MotorcycleRAG API | Application | `1-Presentation/Api` | [README](x) | [docs](y) | API maintainers | 2026-07-21 | Current |
                                  | MotorcycleRAG system | System | repository root | [README](x) | [docs](y) | Maintainers | 2026-07-21 | Current |
                                  """;

    /// <summary>A forecast Review section: it talks about a future closeout but holds no mapping.</summary>
    public const string ReviewForecastSection = """
                                                ## Review

                                                The closeout will be recorded here during Review.
                                                """;

    /// <summary>A Design harvest section with prose but no waiver, for the no-design-change scale.</summary>
    public const string WaiverlessDesignSection = """
                                                  ## Design harvest

                                                  No new components; the change refines the existing widget shape.
                                                  """;

    /// <summary>A governed reference document the closeout mapping can link to.</summary>
    public static string TargetDocument(
        string id = "reference/widget",
        string component = Component,
        string lastReviewed = HarvestLastReviewed)
    {
        return $"""
                ---
                id: {id}
                title: {id}
                doc-type: reference
                status: current
                component: {component}
                owner: Maintainers
                last-reviewed: {lastReviewed}
                ---

                # {id}
                """;
    }

    /// <summary>A todo document inside the todo-index folder, for RESIDUAL tracking.</summary>
    public static string TodoDocument(string id = "todo/cleanup")
    {
        return $"""
                ---
                id: {id}
                title: {id}
                doc-type: todo
                status: needs-review
                component: {Component}
                owner: Maintainers
                last-reviewed: {HarvestLastReviewed}
                ---

                # {id}
                """;
    }

    /// <summary>A Decisions table with one approved row per id.</summary>
    public static string DecisionsSection(params string[] ids)
    {
        return "## Decisions\n\n| ID | Question | Answer | Status |\n| --- | --- | --- | --- |\n"
               + string.Join("\n", ids.Select(id => $"| {id} | Question {id} | Answer {id} | Approved |"))
               + "\n";
    }

    /// <summary>A Design harvest table with one change row per id.</summary>
    public static string DesignHarvestSection(params string[] ids)
    {
        return "## Design harvest\n\n| ID | Change |\n| --- | --- |\n"
               + string.Join("\n", ids.Select(id => $"| {id} | Change {id} |"))
               + "\n";
    }

    /// <summary>A Closeout mapping table; the destination is a link, a waiver, or RESIDUAL.</summary>
    public static string CloseoutMappingSection(params (string Ids, string Destination)[] rows)
    {
        return "## Closeout mapping\n\n| ID | Destination |\n| --- | --- |\n"
               + string.Join("\n", rows.Select(row => $"| {row.Ids} | {row.Destination} |"))
               + "\n";
    }

    /// <summary>
    /// An archived plan. Defaults are the fully passing shape; each condition test overrides
    /// exactly one parameter. Empty lines for the impact keys mean the keys are absent. Default
    /// section links are written for the archive folder, one level deeper than the plans folder;
    /// pass <c>linkPrefix: ".."</c> for a plan still sitting in the plans folder.
    /// </summary>
    public static string PlanDocument(
        string id = "plans/2026-10-20-widget",
        string status = "superseded",
        string bodyStatus = "Complete",
        string impactLine = "documentation-impact: extends-component",
        string componentsLine = "impact-components:\n  - MotorcycleRAG API",
        string? sections = null,
        string linkPrefix = "../..")
    {
        return $"""
                ---
                id: {id}
                title: {id}
                doc-type: plan
                status: {status}
                component: {Component}
                owner: API maintainers
                last-reviewed: {HarvestLastReviewed}
                {impactLine}
                {componentsLine}
                ---

                # {id}

                **Status:** {bodyStatus}

                {sections ?? DefaultSections(linkPrefix)}
                """;
    }

    /// <summary>A minimal failing plan used as the control entry beside a passing variant.</summary>
    public static string ControlPlan()
    {
        return PlanDocument("plans/2026-10-21-control", "draft", "Draft", string.Empty, string.Empty, string.Empty);
    }

    private static string DefaultSections(string linkPrefix)
    {
        return DecisionsSection("D1")
               + CloseoutMappingSection(("D1, H1", $"[Widget architecture]({linkPrefix}/reference/widget.md)"))
               + DesignHarvestSection("H1");
    }
}
