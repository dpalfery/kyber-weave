using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs tasks</c>: the one command that derives a plan's task file and its execution digest, so an agent
/// never writes either by hand. The tests assert behaviour — exit code, rule id, that the file validates, that a
/// finding names the command — rather than message wording.
/// </summary>
public sealed class DocsTasksCommandTests
{
    private const string StaleDigest = "KW-DOC-TASKS-003";
    private const string DiffersFromPlan = "KW-DOC-TASKS-002";
    private const string IncompletePrompt = "KW-DOC-TASKS-004";
    private const string MissingTaskFile = "KW-DOC-TASKS-006";
    private const string CommandName = "kyber-weave docs tasks";

    private static DocFixture Plan(string? markdown = null)
    {
        DocFixture fixture = PlanTaskFileProbe.CorpusInMemory(markdown);
        _ = fixture.EnsureDirectory(".git");
        return fixture;
    }

    private static (int Exit, string Output) Run(DocFixture fixture, string artifact, bool write = false, bool check = false)
    {
        DocsTasksSettings settings = new()
        {
            Path = fixture.FullPath(artifact),
            Format = "json",
            Write = write,
            Check = check
        };

        return Execute(fixture, settings);
    }

    private static (int Exit, string Output) Execute(DocFixture fixture, DocsTasksSettings settings)
    {
        TestConsole console = new() { EmitAnsiSequences = false };
        int exit = DocsTestCommands.Tasks(DocsMemoryServicesD.Services(fixture), console).Execute(null!, settings, CancellationToken.None);
        return (exit, console.Output);
    }

    private static string TaskFileOf(DocFixture fixture, string taskFilePath) => fixture.ReadText(taskFilePath);

    /// <summary>The plan may be named by its slug, with or without the date prefix, or by a name or path from any directory.</summary>
    [Theory]
    [InlineData("ready-tasks", ".")]
    [InlineData("2026-11-07-ready-tasks", "6-Docs/plans")]
    [InlineData("2026-11-07-ready-tasks.md", "6-Docs/plans")]
    [InlineData("6-Docs/plans/2026-11-07-ready-tasks.md", "6-Docs/plans")]
    public void ThePlanCanBeNamedInAnyNaturalSpelling(string typed, string workingDirectory)
    {
        using DocFixture fixture = Plan();
        DocsTasksSettings settings = new()
        {
            Path = typed,
            Format = "json",
            Check = false,
            WorkingDirectory = fixture.FullPath(workingDirectory)
        };

        (int exit, string output) = Execute(fixture, settings);

        Assert.True(exit == 0, output);
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.Equal(PlanTaskFileProbe.PlanPath, report.RootElement.GetProperty("metrics").GetProperty("plan").GetString());
    }

    /// <summary>The generated file is what the validator reads: no task-file finding at all.</summary>
    [Fact]
    public void AGeneratedFilePassesEveryTaskFileRule()
    {
        using DocFixture fixture = Plan();

        (int exit, string output) = Run(fixture, PlanTaskFileProbe.PlanPath, write: true);
        ReportedFindings validated = new(fixture);

        Assert.True(exit == 0, output);
        Assert.DoesNotContain("KW-DOC-TASKS", validated.Output, StringComparison.Ordinal);

        PlanTaskFile? file = PlanTaskFileProbe.Deserialize(
            PlanTaskFileProbe.TaskFilePath,
            TaskFileOf(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.NotNull(file);
        Assert.Equal(
            PlanTaskFileProbe.ExecutionDigest(PlanTaskFileProbe.PlanMarkdown()),
            file.ExecutionDigest);
        Assert.Equal(2, file.Tasks?.Count);
    }

    /// <summary>A second write changes nothing: the file is rewritten whole, never appended or reordered.</summary>
    [Fact]
    public void WritingTwiceLeavesTheFileUntouched()
    {
        using DocFixture fixture = Plan();
        Run(fixture, PlanTaskFileProbe.PlanPath, write: true);
        string path = fixture.FullPath(PlanTaskFileProbe.TaskFilePath);
        string first = fixture.ReadText(PlanTaskFileProbe.TaskFilePath);
        // Any write to the task file from here on fails the run, so an unchanged file proves it was not rewritten.
        fixture.Memory!.FailWhen(
            operation => operation.Path == path && operation.Name.StartsWith("Write", StringComparison.Ordinal),
            new IOException("the task file was rewritten"));

        (int exit, string output) = Run(fixture, PlanTaskFileProbe.PlanPath, write: true);

        Assert.True(exit == 0, output);
        Assert.Equal(first, fixture.ReadText(PlanTaskFileProbe.TaskFilePath));
    }

    /// <summary>
    /// <c>--check</c> passes on a current file and, once the plan moves, exits 1 with the stale-digest rule and the
    /// command that fixes it. The control is the first half: a check that failed everything would pass the second.
    /// </summary>
    [Fact]
    public void CheckDetectsAStaleDigestAndNamesTheCommand()
    {
        using DocFixture fixture = Plan();
        Run(fixture, PlanTaskFileProbe.PlanPath, write: true);
        Assert.Equal(0, Run(fixture, PlanTaskFileProbe.PlanPath, check: true).Exit);

        fixture.Write(
            PlanTaskFileProbe.PlanPath,
            PlanTaskFileProbe.PlanMarkdown().Replace("The task file validator", "The renamed validator", StringComparison.Ordinal));
        (int exit, string output) = Run(fixture, PlanTaskFileProbe.PlanPath, check: true);

        Assert.Equal(1, exit);
        Assert.Contains(StaleDigest, output, StringComparison.Ordinal);
        Assert.Contains(CommandName, output, StringComparison.Ordinal);
        Assert.Contains(DiffersFromPlan, output, StringComparison.Ordinal);
    }

    /// <summary>A hand-edited prompt is a difference: the file is the plan's output, not an authored document.</summary>
    [Fact]
    public void CheckDetectsAHandEditedPrompt()
    {
        using DocFixture fixture = Plan();
        Run(fixture, PlanTaskFileProbe.PlanPath, write: true);
        string edited = TaskFileOf(fixture, PlanTaskFileProbe.TaskFilePath).Replace("Task T1", "Task T1 (edited)", StringComparison.Ordinal);
        fixture.Write(PlanTaskFileProbe.TaskFilePath, edited);

        (int exit, string output) = Run(fixture, PlanTaskFileProbe.PlanPath, check: true);

        Assert.Equal(1, exit);
        Assert.Contains(DiffersFromPlan, output, StringComparison.Ordinal);
    }

    /// <summary>A missing file is reported under the missing-file rule with the command, not silently passed.</summary>
    [Fact]
    public void CheckReportsAMissingFile()
    {
        using DocFixture fixture = Plan();

        (int exit, string output) = Run(fixture, PlanTaskFileProbe.PlanPath, check: true);

        Assert.Equal(1, exit);
        Assert.Contains(MissingTaskFile, output, StringComparison.Ordinal);
        Assert.Contains(CommandName, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plan task with no command and no acceptance criteria anywhere is a gap in the plan. Nothing is written,
    /// and the finding names the task and the command to run again.
    /// </summary>
    [Fact]
    public void APlanGapNamesTheTaskAndWritesNothing()
    {
        string bare = PlanTaskFileProbe.PlanMarkdown()
            .Replace("`dotnet test --filter FullyQualifiedName~PlanTaskFileValidatorTests`", "none", StringComparison.Ordinal)
            .Replace("| Passes. |", "| |", StringComparison.Ordinal)
            .Replace("| The task file is validated. |", "| |", StringComparison.Ordinal)
            .Replace("| The file is checked against its plan. |", "| |", StringComparison.Ordinal);
        using DocFixture fixture = Plan(bare);

        (int exit, string output) = Run(fixture, PlanTaskFileProbe.PlanPath, write: true);

        Assert.Equal(1, exit);
        Assert.Contains(IncompletePrompt, output, StringComparison.Ordinal);
        Assert.Contains("T1", output, StringComparison.Ordinal);
        Assert.Contains("T2", output, StringComparison.Ordinal);
        Assert.Contains(CommandName, output, StringComparison.Ordinal);
        Assert.False(fixture.Exists(PlanTaskFileProbe.TaskFilePath));
    }

    /// <summary>
    /// The labels an author naturally writes are accepted in the plan: <c>Done when</c> and a bold
    /// <c>Acceptance</c> are restated under the one label the validator reads, so the generated file validates.
    /// </summary>
    [Theory]
    [InlineData("- **Done when:** the class reports a non-zero total.")]
    [InlineData("- **Acceptance:** the class reports a non-zero total.")]
    [InlineData("- Definition of done: the class reports a non-zero total.")]
    public void AcceptanceLabelVariantsInThePlanAreAccepted(string line)
    {
        string plan = PlanTaskFileProbe.PlanMarkdown()
            .Replace("| Passes. |", "| |", StringComparison.Ordinal)
            .Replace("| The task file is validated. |", "| |", StringComparison.Ordinal)
            .Replace("| The file is checked against its plan. |", "| |", StringComparison.Ordinal)
            .Replace("- **Required skills:** `csharp-dev`", "- **Required skills:** `csharp-dev`\n" + line, StringComparison.Ordinal)
            .Replace("- **Required skills:** `test-dev`", "- **Required skills:** `test-dev`\n" + line, StringComparison.Ordinal);
        using DocFixture fixture = Plan(plan);

        (int exit, _) = Run(fixture, PlanTaskFileProbe.PlanPath, write: true);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("KW-DOC-TASKS", new ReportedFindings(fixture).Output, StringComparison.Ordinal);
    }

    /// <summary>The same command writes a specification's <c>tasks.json</c> from its <c>tasks.md</c>.</summary>
    [Fact]
    public void ASpecificationTaskFileIsGeneratedAndValidates()
    {
        using DocFixture fixture = SpecTaskFileProbe.CorpusInMemory();
        _ = fixture.EnsureDirectory(".git");

        (int exit, _) = Run(fixture, SpecTaskFileProbe.TasksPath, write: true);

        Assert.Equal(0, exit);
        Assert.True(fixture.Exists(SpecTaskFileProbe.TaskFilePath));
        Assert.DoesNotContain("KW-DOC-TASKS", new ReportedFindings(fixture).Output, StringComparison.Ordinal);
    }

    /// <summary>The validator's findings for a file written by hand name the command that replaces the hand-writing.</summary>
    [Fact]
    public void ValidatorFindingsNameTheCommand()
    {
        using DocFixture stale = Plan();
        stale.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile(digest: new string('0', 64)));
        Assert.Contains(CommandName, new ReportedFindings(stale).Output, StringComparison.Ordinal);

        using DocFixture missing = Plan();
        ReportedFindings run = new(missing);
        Assert.Contains(MissingTaskFile, run.Output, StringComparison.Ordinal);
        Assert.Contains(CommandName, run.Output, StringComparison.Ordinal);

        using DocFixture noCommand = Plan();
        noCommand.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile(missingPromptPart: "acceptance"));
        ReportedFindings prompt = new(noCommand);
        Assert.Contains(IncompletePrompt, prompt.Output, StringComparison.Ordinal);
        Assert.Contains(CommandName, prompt.Output, StringComparison.Ordinal);
    }

    /// <summary>Asking to write and to check at once is a usage error, not a guess.</summary>
    [Fact]
    public void WriteAndCheckTogetherAreRefused()
    {
        using DocFixture fixture = Plan();

        (int exit, string output) = Run(fixture, PlanTaskFileProbe.PlanPath, write: true, check: true);

        Assert.Equal(1, exit);
        Assert.Contains(DocsTasksCommand.ConflictingModes, output, StringComparison.Ordinal);
    }
}
