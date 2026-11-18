using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// The one service that builds a task prompt: the task's own text, the host facts, the shared rules, the
/// parent's rules and skills, then the technology's rules, skills and standard, each inside a named tag, and
/// what the budget cut last. Nothing is written to the repository.
/// </summary>
public sealed class TaskPromptServiceTests
{
    private const string ConfigYaml = """
        ontology:
          docs-root: 6-Docs
          technologies: [csharp, react, typescript, cobol]
        """;

    private static KyberWeaveConfig Config(string yaml = ConfigYaml) => KyberWeaveConfigAssembler.FromYaml(yaml);

    /// <summary>A corpus with a Ready plan whose tasks name <paramref name="technology"/>, and a task file.</summary>
    private static DocFixture Ready(string technology = "csharp", string? planMarkdown = null)
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(planMarkdown ?? PlanNaming(technology));
        Standards(fixture);
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        DocsTestCommands.Tasks().Execute(
            null!,
            new DocsTasksSettings
            {
                Path = Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath),
                Format = "json",
                Write = true
            },
            CancellationToken.None);
        return fixture;
    }

    /// <summary>The fixture plan with a technology on both its tasks.</summary>
    private static string PlanNaming(string technology) =>
        PlanTaskFileProbe.PlanMarkdown()
            .Replace(
                "- **Depends on:** none",
                $"- **Depends on:** none\n- **Technology:** {technology}",
                StringComparison.Ordinal);

    private static void Standards(DocFixture fixture)
    {
        fixture.Write("6-Docs/standards/shared-rules.md", Document(
            "standards/shared-rules", "Shared rules", "rule",
            "# Shared rules\n\nSHARED-RULES-MARKER: these hold for every technology."));

        fixture.Write("6-Docs/standards/csharp/csharp.standards.md", Document(
            "standards/csharp", "C# coding standard", "coding-standard",
            """
            # C# coding standard

            STANDARD-LEAD-MARKER: how C# is written here.

            ## Naming and layout

            STANDARD-NAMING-MARKER: names use PascalCase.

            ## Testing

            STANDARD-TESTING-MARKER: tests use xUnit.
            """,
            technology: "csharp"));
        fixture.Write("6-Docs/standards/csharp/csharp.rules.md", Document(
            "standards/csharp/rules", "csharp rules", "rule",
            "# csharp rules\n\nCSHARP-RULES-MARKER: nullable is on.",
            technology: "csharp"));
        fixture.Write("6-Docs/standards/csharp/csharp.skills.md", Document(
            "standards/csharp/skills", "csharp skills", "skill",
            "# csharp skills\n\nCSHARP-SKILLS-MARKER: run dotnet format.",
            technology: "csharp"));

        fixture.Write("6-Docs/standards/typescript/typescript.rules.md", Document(
            "standards/typescript/rules", "typescript rules", "rule",
            "# typescript rules\n\nTYPESCRIPT-RULES-MARKER: strict mode is on.",
            technology: "typescript"));
        fixture.Write("6-Docs/standards/typescript/typescript.skills.md", Document(
            "standards/typescript/skills", "typescript skills", "skill",
            "# typescript skills\n\nTYPESCRIPT-SKILLS-MARKER: run tsc.",
            technology: "typescript"));

        fixture.Write("6-Docs/standards/react/react.standards.md", Document(
            "standards/react", "React coding standard", "coding-standard",
            "# React coding standard\n\nREACT-STANDARD-MARKER: components are functions.",
            technology: "react", extra: "builds-on: typescript"));
        fixture.Write("6-Docs/standards/react/react.rules.md", Document(
            "standards/react/rules", "react rules", "rule",
            "# react rules\n\nREACT-RULES-MARKER: no class components.",
            technology: "react"));
        fixture.Write("6-Docs/standards/react/react.skills.md", Document(
            "standards/react/skills", "react skills", "skill",
            "# react skills\n\nREACT-SKILLS-MARKER: render with testing-library.",
            technology: "react"));
    }

    private static string Document(
        string id,
        string title,
        string docType,
        string body,
        string? technology = null,
        string extra = "")
    {
        string technologyLine = technology is null ? string.Empty : $"technology: {technology}\n";
        string extraLine = extra.Length == 0 ? string.Empty : extra + "\n";
        return $"""
            ---
            id: {id}
            title: {title}
            doc-type: {docType}
            status: current
            {technologyLine}{extraLine}owner: Maintainers
            last-reviewed: 2026-11-16
            ---

            {body}
            """;
    }

    private static TaskPromptService Service(DocFixture fixture, KyberWeaveConfig config)
    {
        IServiceProvider provider = DocsTestCommands.Services();
        DocumentIndex index = DocumentIndex.Build(
            RealDocumentLoader.Create(fixture.Root, config.Ontology).Load(), FakeCodeGraphResolver.WithSymbols());
        return new TaskPromptService(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IWritableFileSystem>(),
            index.StandardsFor,
            provider.GetRequiredService<IProcessEnvironment>());
    }

    private static TaskPromptResult Build(DocFixture fixture, string? taskId = "T1", string? technology = null)
    {
        KyberWeaveConfig config = Config();
        return Service(fixture, config).Build(
            new TaskPromptRequest(fixture.Root, TaskId: taskId, Technology: technology),
            config);
    }

    [Fact]
    public void ThePartsAppearInTheStatedOrderEachInsideItsTag()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        string[] tags = ["task", "host-facts", "shared-rules", "technology-rules", "skills", "standard", "left-out"];
        int previous = -1;
        foreach (string tag in tags)
        {
            int open = result.Prompt.IndexOf($"<{tag}>", StringComparison.Ordinal);
            int close = result.Prompt.IndexOf($"</{tag}>", StringComparison.Ordinal);
            Assert.True(open > previous, $"<{tag}> should follow the part before it");
            Assert.True(close > open, $"</{tag}> should close <{tag}>");
            previous = open;
        }

        Assert.Contains("SHARED-RULES-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("CSHARP-RULES-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("CSHARP-SKILLS-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("STANDARD-TESTING-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("## Host facts", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<parent-rules>", result.Prompt, StringComparison.Ordinal);
        Assert.Empty(result.LeftOut);
    }

    [Fact]
    public void TheTaskPartIsTheTaskFilePromptWithItsSlotsFilled()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        string task = Between(result.Prompt, "task");
        Assert.Contains("Task T1", task, StringComparison.Ordinal);
        Assert.Contains(fixture.Root, task, StringComparison.Ordinal);
        Assert.DoesNotContain("{worktree}", task, StringComparison.Ordinal);
    }

    [Fact]
    public void AParentTechnologysRulesAndSkillsComeBeforeTheTechnologysOwn()
    {
        using DocFixture fixture = Ready("react");

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        string parent = Between(result.Prompt, "parent-rules");
        Assert.Contains("TYPESCRIPT-RULES-MARKER", parent, StringComparison.Ordinal);
        Assert.Contains("TYPESCRIPT-SKILLS-MARKER", parent, StringComparison.Ordinal);
        Assert.Contains("REACT-SKILLS-MARKER", Between(result.Prompt, "skills"), StringComparison.Ordinal);
        Assert.Equal("react", result.Technology);
        Assert.True(
            result.Prompt.IndexOf("</shared-rules>", StringComparison.Ordinal)
            < result.Prompt.IndexOf("<parent-rules>", StringComparison.Ordinal));
        Assert.True(
            result.Prompt.IndexOf("</parent-rules>", StringComparison.Ordinal)
            < result.Prompt.IndexOf("<technology-rules>", StringComparison.Ordinal));
    }

    [Fact]
    public void ATechnologyWithNoFolderSaysSoInItsPartAndTheRestIsStillBuilt()
    {
        using DocFixture fixture = Ready("cobol");

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        Assert.Contains("standards folder", Between(result.Prompt, "technology-rules"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cobol", Between(result.Prompt, "technology-rules"), StringComparison.Ordinal);
        Assert.Contains("Task T1", Between(result.Prompt, "task"), StringComparison.Ordinal);
        Assert.Contains("## Host facts", Between(result.Prompt, "host-facts"), StringComparison.Ordinal);
        Assert.Equal("cobol", result.Technology);
    }

    [Fact]
    public void ATechnologyAndNoTaskIdYieldsThePartsFromTheSharedRulesOn()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = Build(fixture, taskId: null, technology: "csharp");

        Assert.True(result.Built, result.Refusal);
        Assert.StartsWith("<shared-rules>", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<host-facts>", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("SHARED-RULES-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("CSHARP-RULES-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("CSHARP-SKILLS-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("STANDARD-NAMING-MARKER", result.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A standard may open directly with its first <c>##</c> heading. That heading is a section
    /// like the others, not a lead: treating it as one would print the first section twice, the
    /// second copy uncuttable.
    /// </summary>
    [Fact]
    public void AStandardThatOpensWithASectionIsNotPrintedTwice()
    {
        using DocFixture fixture = Ready();
        fixture.Write("6-Docs/standards/csharp/csharp.standards.md", Document(
            "standards/csharp", "C# coding standard", "coding-standard",
            """
            ## Naming and layout

            LEADLESS-MARKER: names use PascalCase.

            ## Testing

            LEADLESS-TESTING-MARKER: tests use xUnit.
            """,
            technology: "csharp"));

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        string standard = Between(result.Prompt, "standard");
        Assert.Equal(2, standard.Split("LEADLESS-MARKER").Length);
        Assert.Equal(2, standard.Split("## Naming and layout").Length);
    }

    [Fact]
    public void NeitherATaskIdNorATechnologyBuildsNoPromptAndSaysWhy()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = Build(fixture, taskId: null);

        Assert.False(result.Built);
        Assert.Empty(result.Prompt);
        Assert.Contains("task id", result.Refusal, StringComparison.Ordinal);
        Assert.Contains("technology", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleTaskFileBuildsNoPromptAndNamesTheRepairCommand()
    {
        using DocFixture fixture = Ready();
        fixture.Write(
            PlanTaskFileProbe.PlanPath,
            PlanTaskFileProbe.PlanMarkdown()
                .Replace("The task file validator", "The renamed validator", StringComparison.Ordinal)
                .Replace(
                    "- **Depends on:** none",
                    "- **Depends on:** none\n- **Technology:** csharp",
                    StringComparison.Ordinal));

        TaskPromptResult result = Build(fixture);

        Assert.False(result.Built);
        Assert.Empty(result.Prompt);
        Assert.Contains("docs tasks " + PlanTaskFileProbe.PlanPath + " --write", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTaskBuildsNoPromptAndListsTheTasksThePlanHas()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = Build(fixture, taskId: "T9");

        Assert.False(result.Built);
        Assert.Contains("T1", result.Refusal, StringComparison.Ordinal);
        Assert.Contains("T2", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void PriorResultsFromTheRunsScratchAreaReachTheTaskPart()
    {
        using DocFixture fixture = Ready();
        string scratch = Path.Combine(fixture.Root, TaskPromptService.ScratchFolder);
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "results.txt"), "T1 reported: built, four tests green.");
        KyberWeaveConfig config = Config();

        TaskPromptResult result = Service(fixture, config).Build(
            new TaskPromptRequest(
                fixture.Root,
                TaskId: "T1",
                PriorResultsPath: $"{TaskPromptService.ScratchFolder}/results.txt"),
            config);

        Assert.True(result.Built, result.Refusal);
        Assert.Contains("T1 reported: built, four tests green.", Between(result.Prompt, "task"), StringComparison.Ordinal);
    }

    /// <summary>The dispatch's failed-attempt count reaches the worker inside the prompt, not only the request.</summary>
    [Fact]
    public void TheFailedAttemptCountReachesThePrompt()
    {
        using DocFixture fixture = Ready();
        KyberWeaveConfig config = Config();

        TaskPromptResult result = Service(fixture, config).Build(
            new TaskPromptRequest(fixture.Root, TaskId: "T1", FailedAttempts: 2),
            config);

        Assert.True(result.Built, result.Refusal);
        Assert.Contains("FAILED_ATTEMPTS: 2", Between(result.Prompt, "task"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A symlink inside the scratch area must not become a window onto any readable file: the path the worker's
    /// prompt reads is the resolved one, and it must still sit under the scratch area.
    /// </summary>
    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public void APriorResultsSymlinkOutOfTheScratchAreaIsRefused(string linked)
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Symbolic-link creation requires platform-specific privileges on Windows.");

        using DocFixture fixture = Ready();
        string scratch = Path.Combine(fixture.Root, TaskPromptService.ScratchFolder);
        if (linked == "file")
        {
            string secret = Path.Combine(fixture.Root, "results.txt");
            File.WriteAllText(secret, "a tracked file the worker must not receive.");
            Directory.CreateDirectory(Path.Combine(scratch, "t1"));
            File.CreateSymbolicLink(Path.Combine(scratch, "t1", "link.md"), secret);
        }
        else
        {
            string outside = Path.Combine(fixture.Root, "elsewhere");
            Directory.CreateDirectory(outside);
            File.WriteAllText(
                Path.Combine(outside, "link.md"),
                "a tracked file the worker must not receive.");
            Directory.CreateDirectory(scratch);
            Directory.CreateSymbolicLink(Path.Combine(scratch, "t1"), outside);
        }

        KyberWeaveConfig config = Config();

        TaskPromptResult result = Service(fixture, config).Build(
            new TaskPromptRequest(
                fixture.Root,
                TaskId: "T1",
                PriorResultsPath: $"{TaskPromptService.ScratchFolder}/t1/link.md"),
            config);

        Assert.False(result.Built);
        Assert.Contains(TaskPromptService.ScratchFolder, result.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The prior-results slot reads only the run's own scratch area: a tracked file, an absolute path outside the
    /// repository and a path that climbs out with <c>..</c> are refused before anything is read.
    /// </summary>
    [Theory]
    [InlineData("results.txt")]
    [InlineData("../outside-results.txt")]
    [InlineData("/etc/hostname")]
    public void APriorResultsPathOutsideTheScratchAreaIsRefused(string relative)
    {
        using DocFixture fixture = Ready();
        File.WriteAllText(Path.Combine(fixture.Root, "results.txt"), "a tracked file the worker must not receive.");
        KyberWeaveConfig config = Config();

        TaskPromptResult result = Service(fixture, config).Build(
            new TaskPromptRequest(fixture.Root, TaskId: "T1", PriorResultsPath: relative),
            config);

        Assert.False(result.Built);
        Assert.Contains(TaskPromptService.ScratchFolder, result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDifficultyAndItsSourceComeFromTheTaskFile()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal("medium", result.Difficulty);
        Assert.Equal("default", result.DifficultySource);
    }

    [Fact]
    public void TheRemainingInTheLeftOutPartSaysNothingWasLeftOut()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = Build(fixture);

        Assert.Contains("Nothing was left out", Between(result.Prompt, "left-out"), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildingAPromptWritesNoFile()
    {
        using DocFixture fixture = Ready();
        Dictionary<string, (long Length, DateTime Written)> before = Snapshot(fixture.Root);

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    private static Dictionary<string, (long Length, DateTime Written)> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(root, file).Replace('\\', '/'),
                file => (new FileInfo(file).Length, File.GetLastWriteTimeUtc(file)),
                StringComparer.Ordinal);

    private static string Between(string prompt, string tag)
    {
        int open = prompt.IndexOf($"<{tag}>\n", StringComparison.Ordinal);
        int close = prompt.IndexOf($"</{tag}>", StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, $"the prompt should carry a <{tag}> part");
        return prompt[(open + tag.Length + 3)..close];
    }
}
