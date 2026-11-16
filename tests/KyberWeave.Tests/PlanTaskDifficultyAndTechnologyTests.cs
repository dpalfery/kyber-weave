using System.Text.Json;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A plan or specification task may carry <c>Difficulty:</c> and <c>Technology:</c> lines. The
/// parser carries them on the task, the generator writes them into the task file entry, and a task
/// that names neither still writes a usable entry: <c>medium</c> with source <c>default</c> and no
/// technology. A task file from an older release, without the new fields, still loads.
/// </summary>
public sealed class PlanTaskDifficultyAndTechnologyTests
{
    private const string DifficultyTechnology = """
        - **Difficulty:** hard
        - **Technology:** csharp
        """;

    [Fact]
    public void DifficultyAndTechnologyLinesReachTheParsedTask()
    {
        PlanDocument plan = PlanDocumentParser.Parse(Plan(DifficultyTechnology));

        PlanTask task = Assert.Single(plan.Tasks);
        Assert.Equal("hard", task.Difficulty);
        Assert.Equal("csharp", task.Technology);
    }

    [Fact]
    public void DifficultyAndTechnologyReachTheTaskFileEntry()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus(Plan(DifficultyTechnology));

        PlanTaskFileEntry entry = OnlyEntry(fixture);

        Assert.Equal("hard", entry.Difficulty);
        Assert.Equal("architect", entry.DifficultySource);
        Assert.Equal("csharp", entry.Technology);
    }

    [Fact]
    public void ATaskWithNeitherLineGetsMediumFromTheDefaultAndNoTechnology()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus(Plan(extra: null));

        PlanTaskFileEntry entry = OnlyEntry(fixture);

        Assert.Equal("medium", entry.Difficulty);
        Assert.Equal("default", entry.DifficultySource);
        Assert.Null(entry.Technology);
    }

    [Fact]
    public void AnOlderVersionTaskFileWithoutTheNewFieldsStillLoads()
    {
        const string Older = """
            {
              "schema-version": 1,
              "plan": "6-Docs/plans/2026-11-07-ready-tasks.md",
              "execution-digest": "f353dda4e88a04221c68694e74e39e58494738a2c95e515bf42ac23a5bbe6b44",
              "tasks": [
                {
                  "id": "T1",
                  "capabilities": "C# parsing, with xUnit tests",
                  "files": ["src/Placeholder.cs"],
                  "session": "T1",
                  "prompt": "Task T1"
                }
              ]
            }
            """;

        PlanTaskFile file = Assert.IsType<PlanTaskFile>(JsonSerializer.Deserialize<PlanTaskFile>(Older));
        Assert.NotNull(file.Tasks);
        PlanTaskFileEntry entry = Assert.Single(file.Tasks);
        Assert.Equal(1, file.SchemaVersion);
        Assert.Null(entry.Difficulty);
        Assert.Null(entry.DifficultySource);
        Assert.Null(entry.Technology);
    }

    private static PlanTaskFileEntry OnlyEntry(DocFixture fixture)
    {
        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(
            fixture.Root,
            KyberWeaveConfig.ProductDefaults,
            DocsPorts.Files,
            DocsPorts.Process).Run(PlanTaskFileProbe.PlanPath, PlanTaskFileMode.Preview);

        Assert.Null(outcome.Report.Items.FirstOrDefault(item => item.Severity == Core.Diagnostics.Severity.Error));
        PlanTaskFile file = Assert.IsType<PlanTaskFile>(outcome.File);
        Assert.NotNull(file.Tasks);
        return Assert.Single(file.Tasks);
    }

    private static string Plan(string? extra) =>
        $$"""
        ---
        id: plans/2026-11-07-ready-tasks
        title: Ready tasks
        doc-type: plan
        status: current
        component: Difficulty fixture
        owner: Maintainers
        last-reviewed: 2026-11-16
        development-mode: test-first
        ---

        # Ready tasks

        ## Test contract

        | Task | Test project or file | Runner filter | Observable behaviour | RED evidence required | GREEN acceptance |
        |---|---|---|---|---|---|
        | T1 | `tests/KyberWeave.Tests/PlanTaskDifficultyAndTechnologyTests.cs` | `dotnet test --filter FullyQualifiedName~PlanTaskDifficultyAndTechnologyTests` | The entry carries the fields. | Fails: the fields do not exist. | Passes. |

        ## Tasks

        ### T1: Read the task fields

        - **Files:** `src/Placeholder.cs`
        - **Depends on:** none
        - **Capabilities:** C# parsing and serialization, with xUnit tests
        {{extra}}

        ## Out of scope

        - `docs/archive/**`
        """;
}
