using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A worker sees only the packet the task file hands it and may not open the plan, so the packet must carry the Test
/// contract row and Decisions rows its task cites, and the generator must say at generation time what it could not carry.
/// </summary>
public sealed class PlanTaskPacketTests
{
    private const string Plan = "docs/plans/2026-11-13-widget.md";

    private static string Markdown(string decisions, string contract, string tasks) => $"""
        ---
        id: plans/2026-11-13-widget
        title: Widget
        doc-type: plan
        status: current
        owner: Maintainers
        last-reviewed: 2026-11-13
        ---

        # Widget

        ## Decisions

        | ID | Decision | Rationale |
        |---|---|---|
        {decisions}

        ## Styling

        The widget is 320px wide and sits left of the chart.

        ## Test contract

        | Task | Test project or file | Runner filter | Observable behaviour | RED evidence required | GREEN acceptance |
        |---|---|---|---|---|---|
        {contract}

        ## Tasks

        {tasks}
        """;

    private static (PlanTaskFile? File, DiagnosticReport Report) Build(string markdown, string? repoRoot = null)
    {
        DiagnosticReport report = new();
        PlanTaskFile? file = PlanTaskFileGenerator.Build(Plan, markdown, ["docs/plans"], report, repoRoot);
        return (file, report);
    }

    private const string Decisions =
        "| D5 | Place the widget left of the chart | Matches the dashboard grid |\n"
        + "| D-7 | Show an inline error state | Users miss toasts |\n"
        + "| D9 | Unrelated | Not cited |";

    [Fact]
    public void ThePacketCarriesTheContractRowAndEveryCitedDecisionRowVerbatim()
    {
        string tasks = """
            ### T1: Widget tests

            - **Files:** `tests/Widget.Tests/WidgetTests.cs`
            - **Depends on:** none
            - **Required skills:** `test-dev`

            Write the widget tests. Honour decision 5 and D7.
            """;
        string contract = "| T1 | `tests/Widget.Tests/WidgetTests.cs` | `dotnet test --filter FullyQualifiedName~WidgetTests` | Renders in the left column; shows the error state | Fails: no widget | Passes. |";

        (PlanTaskFile? file, DiagnosticReport report) = Build(Markdown(Decisions, contract, tasks));

        Assert.NotNull(file);
        string prompt = file.Tasks![0].Prompt!;
        Assert.Contains(contract, prompt, StringComparison.Ordinal);
        Assert.Contains("| Task | Test project or file | Runner filter |", prompt, StringComparison.Ordinal);
        Assert.Contains("| D5 | Place the widget left of the chart | Matches the dashboard grid |", prompt, StringComparison.Ordinal);
        Assert.Contains("| D-7 | Show an inline error state | Users miss toasts |", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("D9", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, item => item.Code is "KW-DOC-TASKS-008" or "KW-DOC-TASKS-009");
    }

    [Fact]
    public void ASectionTheTaskNamesByHeadingIsInlinedAndTheRegenerationIsIdentical()
    {
        string tasks = """
            ### T1: Widget

            - **Files:** `src/Widget.cs`
            - **Depends on:** none
            - **Required skills:** `csharp-dev`

            Build it as laid out in the "Styling" section. Command: `dotnet test --filter FullyQualifiedName~Widget`
            Acceptance criteria: it renders.
            """;
        string markdown = Markdown(Decisions, "| T1 | `x` | `dotnet test x` | y | z | w |", tasks);

        (PlanTaskFile? first, _) = Build(markdown);
        (PlanTaskFile? second, _) = Build(markdown);

        Assert.Contains("The widget is 320px wide and sits left of the chart.", first!.Tasks![0].Prompt, StringComparison.Ordinal);
        Assert.Equal(PlanTaskFileJson.Serialize(first), PlanTaskFileJson.Serialize(second!));
    }

    [Fact]
    public void ACitedDecisionWithNoRowIsAWarningNamingTheTaskTheIdAndAnExample()
    {
        string tasks = """
            ### T1: Widget

            - **Files:** `src/Widget.cs`
            - **Depends on:** none
            - **Required skills:** `csharp-dev`

            Follow D12. Command: `dotnet build src`
            Acceptance criteria: it builds.
            """;

        (PlanTaskFile? file, DiagnosticReport report) = Build(Markdown(Decisions, "| T1 | `x` | `dotnet test x` | y | z | w |", tasks));

        Assert.NotNull(file);
        Diagnostic finding = Assert.Single(report.Items, item => item.Code == "KW-DOC-TASKS-008");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("T1", finding.Subject);
        Assert.Contains("D12", finding.Message, StringComparison.Ordinal);
        Assert.Contains("| D12 |", finding.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void APointerWithNoTargetIsAWarningButOneThatNamesAResolvableIdIsNot()
    {
        string tasks = """
            ### T1: Widget

            - **Files:** `src/Widget.cs`
            - **Depends on:** none
            - **Required skills:** `csharp-dev`

            Lay it out as described above.
            Handle errors as specified in the plan.
            Colours follow D5 as described above.
            Command: `dotnet build src`
            Acceptance criteria: it builds.
            """;

        (_, DiagnosticReport report) = Build(Markdown(Decisions, "| T1 | `x` | `dotnet test x` | y | z | w |", tasks));

        Diagnostic[] vague = [.. report.Items.Where(item => item.Code == "KW-DOC-TASKS-009")];
        Assert.Equal(2, vague.Length);
        Assert.All(vague, finding => Assert.Equal("T1", finding.Subject));
        Assert.Contains(vague, finding => finding.Message.Contains("as described above", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vague, finding => finding.Message.Contains("as specified in the plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARowTooLongForThePacketIsLeftOutByNameAndReported()
    {
        string longRationale = new('x', 7000);
        string decisions = $"| D5 | Big | {longRationale} |";
        string tasks = """
            ### T1: Widget

            - **Files:** `src/Widget.cs`
            - **Depends on:** none
            - **Required skills:** `csharp-dev`

            Follow D5. Command: `dotnet build src`
            Acceptance criteria: it builds.
            """;

        (PlanTaskFile? file, DiagnosticReport report) = Build(Markdown(decisions, "| T1 | `x` | `dotnet test x` | y | z | w |", tasks));

        string prompt = file!.Tasks![0].Prompt!;
        Assert.DoesNotContain(longRationale, prompt, StringComparison.Ordinal);
        Assert.Contains("Left out", prompt, StringComparison.Ordinal);
        Assert.Contains("decision D5", prompt, StringComparison.Ordinal);
        Assert.Contains(report.Items, item => item.Code == "KW-DOC-TASKS-008" && item.Message.Contains("left out", StringComparison.Ordinal));
    }

    /// <summary>
    /// KW-DOC-TASKS-010 is retired: a test-first task's one worker writes the tests and the code they name, so no task's
    /// tests wait on code another task creates, and the generator no longer judges task order by compile unit.
    /// </summary>
    [Fact]
    public void ATaskWhoseTestsNameCodeNoEarlierTaskCreatesIsNotWarnedAboutOrder()
    {
        string tasks = """
            ### T1: Widget repository reads widgets

            - **Files:** `tests/Widget.Tests/WidgetTests.cs`, `src/Widget/WidgetRepository.cs`
            - **Depends on:** none
            - **Required skills:** `csharp-dev`

            Test and implement `src/Widget/WidgetRepository.cs`. Command: `dotnet test tests/Widget.Tests`
            Acceptance criteria: the tests fail before the repository exists and pass after.
            """;
        string contract = "| T1 | `tests/Widget.Tests/WidgetTests.cs` | `dotnet test tests/Widget.Tests` | Reads widgets | Fails: no repository | Passes. |";
        string root = Path.Combine(Path.GetTempPath(), "kw-packet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.DoesNotContain(Build(Markdown(Decisions, contract, tasks), root).Report.Items, item => item.Code == "KW-DOC-TASKS-010");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ProseThatIsNotARepositoryPathIsLeftOutOfAFileScopeAndReportedAtInfoLevel()
    {
        string root = Path.Combine(Path.GetTempPath(), "kw-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src/Widget"));
        Directory.CreateDirectory(Path.Combine(root, "tests/Widget.Tests"));
        Directory.CreateDirectory(Path.Combine(root, "5-Test/tests/HotshotLogistics.Tests/Jobs"));
        File.WriteAllText(Path.Combine(root, "src/Widget/Existing.cs"), "// x");
        File.WriteAllText(Path.Combine(root, "tests/Widget.Tests/WidgetTests.cs"), "// x");
        try
        {
            string tasks = """
                ### T1: Widget

                - **Files:** `src/Widget/Existing.cs`, `src/Widget/NewThing.cs`, `src/Widget/`, `tests/page.route`, `https://localhost:5001/api/job`, `/api/drivers`, `/api/billing/invoices/overdue`, `page.goto('/')`, `5-Test/tests/HotshotLogistics.Tests/Jobs/HotshotLogistics.Tests.Jobs`, `tests/Widget.Tests/*.cs`, `elsewhere/Brand.cs`
                - **Depends on:** none
                - **Required skills:** `csharp-dev`

                Command: `dotnet build src`
                Acceptance criteria: it builds.
                """;

            (PlanTaskFile? file, DiagnosticReport report) = Build(Markdown(Decisions, "| T1 | `x` | `dotnet test x` | y | z | w |", tasks), root);

            Assert.Equal(
                ["src/Widget/Existing.cs", "src/Widget/NewThing.cs", "src/Widget/**", "tests/Widget.Tests/*.cs"],
                file!.Tasks![0].Files);
            Diagnostic dropped = Assert.Single(report.Items, item => item.Code == "KW-DOC-TASKS-012");
            Assert.Equal(Severity.Info, dropped.Severity);
            foreach (string text in new[] { "tests/page.route", "https://localhost:5001/api/job", "/api/drivers", "/api/billing/invoices/overdue", "page.goto('/')", "HotshotLogistics.Tests.Jobs", "elsewhere/Brand.cs" })
            {
                Assert.Contains(text, dropped.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
