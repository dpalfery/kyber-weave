using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A plan task names the abilities its work needs, not an agent: the task file stores that text, writes a role only
/// when the plan assigns one, and the ready queue shows the text so the conductor can choose the agent (ADR 0080).
/// </summary>
public sealed class PlanTaskCapabilitiesTests
{
    private const string Plan = "docs/plans/2026-11-13-orders.md";

    private static string Markdown(string task) => $"""
        # Orders

        ## Tasks

        ### T1: Add the orders page

        - **Files:** `web/src/Orders.tsx`
        - **Depends on:** none
        {task}

        Build the page. Command: `npm test -- Orders`
        Acceptance criteria: the page lists orders.
        """;

    private static (PlanTaskFileEntry? Entry, DiagnosticReport Report) Build(string task)
    {
        DiagnosticReport report = new();
        PlanTaskFile? file = PlanTaskFileGenerator.Build(Plan, Markdown(task), ["docs/plans"], report);
        return (file?.Tasks?[0], report);
    }

    [Fact]
    public void ACapabilitiesLineIsStoredAndNoRoleIsDerived()
    {
        (PlanTaskFileEntry? entry, _) = Build("- **Capabilities:** React and TypeScript UI with Playwright tests");

        Assert.NotNull(entry);
        Assert.Equal("React and TypeScript UI with Playwright tests", entry.Capabilities);
        Assert.Null(entry.Role);
    }

    [Fact]
    public void AnOlderPlansSkillsLineBecomesTheCapabilityTextWithoutARole()
    {
        (PlanTaskFileEntry? entry, _) = Build("- **Required skills:** `react-dev`, `test-dev`");

        Assert.NotNull(entry);
        Assert.Equal("react-dev, test-dev", entry.Capabilities);
        Assert.Null(entry.Role);
    }

    [Fact]
    public void AnExplicitRoleIsTheOwnersOverrideAndIsKept()
    {
        (PlanTaskFileEntry? entry, _) = Build("- **Capabilities:** React UI\n- **Role:** `react-dev`");

        Assert.NotNull(entry);
        Assert.Equal("react-dev", entry.Role);
        Assert.Equal("React UI", entry.Capabilities);
    }

    [Fact]
    public void ATaskWithNeitherCapabilitiesNorARoleIsAGapNamingTheLineToAdd()
    {
        (PlanTaskFileEntry? entry, DiagnosticReport report) = Build(string.Empty);

        Assert.Null(entry);
        Diagnostic gap = Assert.Single(report.Items, item => item.Code == PlanTaskFileValidator.IncompletePrompt);
        Assert.Contains("Capabilities:", gap.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadyQueueShowsWhatEachTaskNeedsAndARoleOnlyWhenThePlanAssignedOne()
    {
        PlanTaskFileEntry[] tasks =
        [
            new("T1", Capabilities: "SQL Server data access with repositories and integration tests", Session: "a", Prompt: "Task T1: Add the repository\n"),
            new("T2", Role: "docs-dev", Capabilities: "documentation", DependsOn: ["T1"], Prompt: "Task T2: Document it\n"),
        ];

        string[] lines = PlanTaskPacket.ReadyQueue(tasks).Split('\n');

        Assert.Equal("T1: Add the repository | needs: SQL Server data access with repositories and integration tests | after=none | session=a", lines[0]);
        Assert.Equal("T2: Document it | needs: documentation | role=docs-dev | after=T1", lines[1]);
        Assert.Equal("ready now: T1", lines[2]);
        Assert.Equal(
            "T1  needs=SQL Server data access with repositories and integration tests  depends-on=none  session=a  files=0",
            PlanTaskPacket.ListLine(tasks[0]));
    }
}
