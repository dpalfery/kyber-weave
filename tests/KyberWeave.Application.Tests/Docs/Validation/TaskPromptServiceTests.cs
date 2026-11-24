using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using Xunit;

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
        DocFixture fixture = PlanTaskFileProbe.CorpusInMemory(planMarkdown ?? PlanNaming(technology));
        Standards(fixture);
        _ = fixture.EnsureDirectory(".git");
        using TestConsole console = new() { EmitAnsiSequences = false };
        _ = DocsTestCommands.Tasks(DocsMemoryServicesD.Services(fixture), console).Execute(
            null!,
            new DocsTasksSettings
            {
                Path = fixture.FullPath(PlanTaskFileProbe.PlanPath),
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

    private static TaskPromptService Service(DocFixture fixture, KyberWeaveConfig config, IKwSkillCatalog? skills = null)
    {
        IServiceProvider provider = DocsMemoryServicesD.Services(fixture);
        DocumentIndex index = DocumentIndex.Build(
            fixture.Loader(config.Ontology).Load(), FakeCodeGraphResolver.WithSymbols());
        return new TaskPromptService(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IWritableFileSystem>(),
            index.StandardsFor,
            provider.GetRequiredService<IProcessEnvironment>(),
            skills ?? FixedKwSkillCatalog.Empty);
    }

    private static TaskPromptResult Build(DocFixture fixture, string? taskId = "T1", string? technology = null)
    {
        KyberWeaveConfig config = Config();
        return Service(fixture, config).Build(
            new TaskPromptRequest(fixture.Root, TaskId: taskId, Technology: technology),
            config);
    }

    private static TaskPromptResult BuildWith(
        DocFixture fixture,
        IKwSkillCatalog skills,
        string? taskId = "T1",
        string? technology = null,
        string? agent = null,
        string? roleAgent = null)
    {
        KyberWeaveConfig config = Config();
        return Service(fixture, config, skills).Build(
            new TaskPromptRequest(
                fixture.Root,
                TaskId: taskId,
                Technology: technology,
                Agent: agent,
                RoleAgent: roleAgent),
            config);
    }

    /// <summary>A catalog that lists <paramref name="entries"/> for <paramref name="technology"/> and nothing else.</summary>
    private static IKwSkillCatalog SkillsOf(string technology, params KwSkillEntry[] entries) =>
        new FixedKwSkillCatalog((listed, _) => listed == technology
            ? new KwSkillList(entries, [])
            : new KwSkillList([], []));

    private static readonly KwSkillEntry CSharpSkill = new(
        "csharp-format", "CSHARP-SKILL-MARKER: run dotnet format.", KwSkillSource.Shipped);

    private const string LoadInstruction =
        "These are Kyber-Weave skills, not harness skills. When a skill's description fits your work, load it by " +
        "calling the kw-docs load_skill tool with its name; never load one through your harness's own skill tool.";

    [Fact]
    public void AListedSkillAppearsByNameAndDescription()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = BuildWith(fixture, SkillsOf("csharp", CSharpSkill));

        Assert.True(result.Built, result.Refusal);
        Assert.Contains("- csharp-format: CSHARP-SKILL-MARKER: run dotnet format.", Between(result.Prompt, "kw-skills"), StringComparison.Ordinal);
    }

    [Fact]
    public void AChildTechnologyListsItsParentsSkillsThenItsOwn()
    {
        using DocFixture fixture = Ready("react");
        KwSkillEntry typeScript = new("ts-strict", "TS-SKILL-MARKER: keep strict on.", KwSkillSource.Shipped);
        KwSkillEntry react = new("react-tests", "REACT-SKILL-MARKER: render with testing-library.", KwSkillSource.Repository);
        IKwSkillCatalog skills = new FixedKwSkillCatalog((listed, _) => listed == "react"
            ? new KwSkillList([typeScript, react], [])
            : new KwSkillList([], []));

        TaskPromptResult result = BuildWith(fixture, skills);

        Assert.True(result.Built, result.Refusal);
        Assert.Single(Occurrences(result.Prompt, "<kw-skills>"));
        string list = Between(result.Prompt, "kw-skills");
        Assert.True(list.IndexOf("TS-SKILL-MARKER", StringComparison.Ordinal) < list.IndexOf("REACT-SKILL-MARKER", StringComparison.Ordinal));
        Assert.DoesNotContain("TS-SKILL-MARKER", Between(result.Prompt, "parent-rules"), StringComparison.Ordinal);
        Assert.DoesNotContain("<skills>", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Parent skills", result.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSkillsSectionIsOneKwSkillsTagThatOpensWithTheLoadSkillInstruction()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = BuildWith(fixture, SkillsOf("csharp", CSharpSkill));

        Assert.True(result.Built, result.Refusal);
        Assert.Single(Occurrences(result.Prompt, "<kw-skills>"));
        Assert.StartsWith(LoadInstruction + "\n", Between(result.Prompt, "kw-skills"), StringComparison.Ordinal);
        Assert.True(
            result.Prompt.IndexOf("</technology-rules>", StringComparison.Ordinal)
            < result.Prompt.IndexOf("<kw-skills>", StringComparison.Ordinal));
        Assert.True(
            result.Prompt.IndexOf("</kw-skills>", StringComparison.Ordinal)
            < result.Prompt.IndexOf("<standard>", StringComparison.Ordinal));
    }

    [Fact]
    public void ATechnologyWithNoAssignedSkillsGetsNoSkillsSection()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = BuildWith(fixture, FixedKwSkillCatalog.Empty);

        Assert.True(result.Built, result.Refusal);
        Assert.DoesNotContain("<kw-skills>", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<skills>", result.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSectionUsesTheRepositoryCopysDescription()
    {
        using DocFixture fixture = Ready();
        KwSkillEntry repository = new(
            "csharp-format", "The repository's own wording for this skill.", KwSkillSource.Repository);

        TaskPromptResult result = BuildWith(fixture, SkillsOf("csharp", repository));

        Assert.True(result.Built, result.Refusal);
        Assert.Contains("- csharp-format: The repository's own wording for this skill.", Between(result.Prompt, "kw-skills"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARoleDispatchWithNoTaskAndNoTechnologyCarriesItsRolesSkills()
    {
        using DocFixture fixture = Ready();
        KwSkillEntry review = new("review-lens", "ROLE-SKILL-MARKER: check the diff.", KwSkillSource.Shipped);
        IKwSkillCatalog skills = new FixedKwSkillCatalog((technology, role) => technology is null && role == "code-reviewer"
            ? new KwSkillList([review], [])
            : new KwSkillList([], []));

        TaskPromptResult result = BuildWith(fixture, skills, taskId: null, roleAgent: "code-reviewer");

        Assert.True(result.Built, result.Refusal);
        Assert.StartsWith("<kw-skills>\n" + LoadInstruction, result.Prompt, StringComparison.Ordinal);
        Assert.EndsWith("</kw-skills>", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("ROLE-SKILL-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<shared-rules>", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<left-out>", result.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoleWithNoSkillsAndNoTaskBuildsAnEmptyPrompt()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = BuildWith(fixture, FixedKwSkillCatalog.Empty, taskId: null, roleAgent: "docs-dev");

        Assert.True(result.Built, result.Refusal);
        Assert.Empty(result.Prompt);
    }

    [Fact]
    public void ATaskDispatchToARoleAgentListsTechnologyThenRoleSkills()
    {
        using DocFixture fixture = Ready();
        KwSkillEntry technology = new("csharp-format", "CSHARP-SKILL-MARKER: run dotnet format.", KwSkillSource.Shipped);
        KwSkillEntry role = new("review-lens", "ROLE-SKILL-MARKER: check the diff.", KwSkillSource.Shipped);
        IKwSkillCatalog skills = new FixedKwSkillCatalog((listed, roleAgent) => listed == "csharp" && roleAgent == "code-reviewer"
            ? new KwSkillList([technology, role], [])
            : new KwSkillList([], []));

        TaskPromptResult result = BuildWith(fixture, skills, agent: "code-reviewer");

        Assert.True(result.Built, result.Refusal);
        string list = Between(result.Prompt, "kw-skills");
        Assert.True(list.IndexOf("CSHARP-SKILL-MARKER", StringComparison.Ordinal) < list.IndexOf("ROLE-SKILL-MARKER", StringComparison.Ordinal));
    }

    [Fact]
    public void ATieredDeveloperRefusalStillReadsOnlyAgent()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = BuildWith(fixture, FixedKwSkillCatalog.Empty, taskId: null, roleAgent: "csharp-dev");

        Assert.True(result.Built, result.Refusal);
        Assert.Null(result.Refusal);
    }

    private static List<int> Occurrences(string text, string needle)
    {
        List<int> found = [];
        for (int index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            found.Add(index);
        }

        return found;
    }

    [Fact]
    public void ThePartsAppearInTheStatedOrderEachInsideItsTag()
    {
        using DocFixture fixture = Ready();

        TaskPromptResult result = BuildWith(fixture, SkillsOf("csharp", CSharpSkill));

        Assert.True(result.Built, result.Refusal);
        string[] tags = ["task", "host-facts", "shared-rules", "technology-rules", "kw-skills", "standard", "left-out"];
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
        Assert.Contains("CSHARP-SKILL-MARKER", result.Prompt, StringComparison.Ordinal);
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
    public void AParentTechnologysRulesComeBeforeTheTechnologysOwn()
    {
        using DocFixture fixture = Ready("react");

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        string parent = Between(result.Prompt, "parent-rules");
        Assert.Contains("TYPESCRIPT-RULES-MARKER", parent, StringComparison.Ordinal);
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

        KyberWeaveConfig config = Config();
        TaskPromptResult result = Service(fixture, config, SkillsOf("csharp", CSharpSkill)).Build(
            new TaskPromptRequest(fixture.Root, TaskId: null, Technology: "csharp"),
            config);

        Assert.True(result.Built, result.Refusal);
        Assert.StartsWith("<shared-rules>", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<host-facts>", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("SHARED-RULES-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("CSHARP-RULES-MARKER", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("CSHARP-SKILL-MARKER", result.Prompt, StringComparison.Ordinal);
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
        string scratch = fixture.FullPath(TaskPromptService.ScratchFolder);
        fixture.Memory!.CreateDirectory(scratch);
        fixture.Memory.WriteAllText(Path.Combine(scratch, "results.txt"), "T1 reported: built, four tests green.");
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
        using DocFixture fixture = Ready();
        string scratch = fixture.FullPath(TaskPromptService.ScratchFolder);
        if (linked == "file")
        {
            string secret = fixture.FullPath("results.txt");
            fixture.Memory!.WriteAllText(secret, "a tracked file the worker must not receive.");
            fixture.Memory.CreateDirectory(Path.Combine(scratch, "t1"));
            fixture.Memory.CreateFileLink(Path.Combine(scratch, "t1", "link.md"), secret);
        }
        else
        {
            string outside = fixture.FullPath("elsewhere");
            fixture.Memory!.CreateDirectory(outside);
            fixture.Memory.WriteAllText(
                Path.Combine(outside, "link.md"),
                "a tracked file the worker must not receive.");
            fixture.Memory.CreateDirectory(scratch);
            fixture.Memory.CreateDirectoryLink(Path.Combine(scratch, "t1"), outside);
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
        fixture.Write("results.txt", "a tracked file the worker must not receive.");
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
        Dictionary<string, (long Length, DateTime Written)> before = Snapshot(fixture);

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal(before, Snapshot(fixture));
    }

    /// <summary>
    /// The no-write contract covers the folders a recursive file-system enumerate hides by default: a prompt build that
    /// spilled a lock under <c>.git</c> or an artifact under <c>bin</c> must still fail
    /// <see cref="BuildingAPromptWritesNoFile" />.
    /// </summary>
    [Theory]
    [InlineData(".git")]
    [InlineData("bin")]
    [InlineData("obj")]
    public void TheNoWriteSnapshotSeesFilesUnderFoldersARecursiveEnumerateHides(string folder)
    {
        using DocFixture fixture = Ready();
        Dictionary<string, (long Length, DateTime Written)> before = Snapshot(fixture);
        fixture.Write($"{folder}/spilled.txt", "a file the build must not write");

        Assert.NotEqual(before, Snapshot(fixture));
    }

    private static Dictionary<string, (long Length, DateTime Written)> Snapshot(DocFixture fixture)
    {
        InMemoryFileSystem files = fixture.Memory!;
        Dictionary<string, (long Length, DateTime Written)> seen = new(StringComparer.Ordinal);
        Stack<string> pending = new([fixture.Root]);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string file in files.EnumerateFiles(directory, "*", recursive: false))
            {
                seen[Path.GetRelativePath(fixture.Root, file).Replace('\\', '/')] =
                    (files.GetLength(file), files.GetLastWriteTimeUtc(file));
            }

            foreach (string child in files.EnumerateDirectories(directory))
            {
                pending.Push(child);
            }
        }

        return seen;
    }

    /// <summary>
    /// A rules file whose frontmatter does not name its technology is in no bundle, so the rules
    /// part must say the file exists and is invisible, not that the folder has no rules file.
    /// </summary>
    [Fact]
    public void ARulesFileWhoseFrontmatterMissesTheTechnologyIsNamedRatherThanCalledAbsent()
    {
        using DocFixture fixture = Ready();
        fixture.Write("6-Docs/standards/csharp/csharp.rules.md", Document(
            "standards/csharp/rules", "csharp rules", "rule",
            "# csharp rules\n\nINVISIBLE-RULES-MARKER: nullable is on."));

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        string rules = Between(result.Prompt, "technology-rules");
        Assert.DoesNotContain("INVISIBLE-RULES-MARKER", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("has no rules file", rules, StringComparison.Ordinal);
        Assert.Contains("6-Docs/standards/csharp/csharp.rules.md", rules, StringComparison.Ordinal);
        Assert.Contains("doc-type rule", rules, StringComparison.Ordinal);
        Assert.Contains("technology csharp", rules, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs validate", rules, StringComparison.Ordinal);
    }

    /// <summary>A file that really is absent keeps the plain text, so the two cases stay distinct.</summary>
    [Fact]
    public void AnAbsentRulesFileKeepsThePlainNoRulesFileText()
    {
        using DocFixture fixture = Ready();
        fixture.Memory!.DeleteFile(fixture.FullPath("6-Docs/standards/csharp/csharp.rules.md"));

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        Assert.Contains(
            "has no rules file", Between(result.Prompt, "technology-rules"), StringComparison.Ordinal);
    }

    /// <summary>The parent's rules part uses the same wording when its file exists but is invisible.</summary>
    [Fact]
    public void AParentsUncarriedRulesFileIsNamedInTheParentPart()
    {
        using DocFixture fixture = Ready("react");
        fixture.Write("6-Docs/standards/typescript/typescript.rules.md", Document(
            "standards/typescript/rules", "typescript rules", "rule",
            "# typescript rules\n\nINVISIBLE-PARENT-MARKER: strict mode is on."));

        TaskPromptResult result = Build(fixture);

        Assert.True(result.Built, result.Refusal);
        string parent = Between(result.Prompt, "parent-rules");
        Assert.DoesNotContain("INVISIBLE-PARENT-MARKER", parent, StringComparison.Ordinal);
        Assert.DoesNotContain("has no rules file", parent, StringComparison.Ordinal);
        Assert.Contains("6-Docs/standards/typescript/typescript.rules.md", parent, StringComparison.Ordinal);
        Assert.Contains("parent technology 'typescript'", parent, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs validate", parent, StringComparison.Ordinal);
    }

    private static string Between(string prompt, string tag)
    {
        int open = prompt.IndexOf($"<{tag}>\n", StringComparison.Ordinal);
        int close = prompt.IndexOf($"</{tag}>", StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, $"the prompt should carry a <{tag}> part");
        return prompt[(open + tag.Length + 3)..close];
    }
}
