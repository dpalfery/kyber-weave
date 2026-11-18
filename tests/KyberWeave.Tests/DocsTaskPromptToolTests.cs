using KyberWeave.Tests.Configuration;

using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs_build_task_prompt_for_agent_dispatch</c>: the conductor asks which agent to dispatch and gets the
/// agent, the difficulty and its source, the technology and what the budget left out — never the prompt text.
/// <c>docs packet</c> prints that same prompt.
/// </summary>
public sealed class DocsTaskPromptToolTests
{
    internal static DocFixture Ready(string plan = "")
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(plan.Length == 0 ? PlanNaming() : plan);
        Standards(fixture);
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        fixture.Write(".kyber-weave/kyber-weave.yml", ConfigYaml);
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

    private const string ConfigYaml = """
        ontology:
          docs-root: 6-Docs
          technologies: [csharp]
        """;

    private static string PlanNaming() =>
        PlanTaskFileProbe.PlanMarkdown().Replace(
            "- **Depends on:** none",
            "- **Depends on:** none\n- **Technology:** csharp",
            StringComparison.Ordinal);

    private static void Standards(DocFixture fixture)
    {
        fixture.Write("6-Docs/standards/shared-rules.md", Document(
            "standards/shared-rules", "Shared rules", "rule",
            "# Shared rules\n\nSHARED-RULES-MARKER: these hold for every technology.", null));
        fixture.Write("6-Docs/standards/csharp/csharp.standards.md", Document(
            "standards/csharp", "C# coding standard", "coding-standard",
            "# C# coding standard\n\nSTANDARD-MARKER: how C# is written here.\n\n## Testing\n\nTests use xUnit.",
            "csharp"));
        fixture.Write("6-Docs/standards/csharp/csharp.rules.md", Document(
            "standards/csharp/rules", "csharp rules", "rule",
            "# csharp rules\n\nCSHARP-RULES-MARKER: nullable is on.", "csharp"));
        fixture.Write("6-Docs/standards/csharp/csharp.skills.md", Document(
            "standards/csharp/skills", "csharp skills", "skill",
            "# csharp skills\n\nCSHARP-SKILLS-MARKER: run dotnet format.", "csharp"));
    }

    private static string Document(string id, string title, string docType, string body, string? technology)
    {
        string technologyLine = technology is null ? string.Empty : $"technology: {technology}\n";
        return $"""
            ---
            id: {id}
            title: {title}
            doc-type: {docType}
            status: current
            {technologyLine}owner: Maintainers
            last-reviewed: 2026-11-16
            ---

            {body}
            """;
    }

    internal static KyberWeaveConfig Config(DocFixture fixture) => ConfigFileReader.Load(fixture.Root);

    private static DocumentIndexHost Host(DocFixture fixture, KyberWeaveConfig config) => new(
        fixture.Root,
        () => FakeCodeGraphResolver.WithSymbols(),
        () => RealDocumentLoader.Create(fixture.Root, config.Ontology).Load(),
        DocsPorts.Files,
        DocsPorts.Paths,
        DocsPorts.Environment,
        config.Ontology.DocsRoots,
        config.Ontology.ResolvedCatalogPath);

    internal static DocsTools Tools(DocFixture fixture) =>
        DocsToolsComposition.Create(Host(fixture, Config(fixture)));

    internal static TaskPromptService Service(DocFixture fixture)
    {
        IServiceProvider provider = DocsTestCommands.Services();
        KyberWeaveConfig config = Config(fixture);
        return new TaskPromptService(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IFileSystem>(),
            Host(fixture, config).Current().StandardsFor,
            provider.GetRequiredService<IProcessEnvironment>());
    }

    private static string Packet(DocFixture fixture, string task)
    {
        DocsPacketSettings settings = new()
        {
            Task = task,
            WorkingDirectory = fixture.Root,
            ListWorktrees = _ => [fixture.Root]
        };
        return ProcessConsoleCapture.Run(
            () => DocsTestCommands.Packet().Execute(null!, settings, CancellationToken.None)).Output;
    }

    [Fact]
    public void TheToolNamesTheAgentDifficultyTechnologyAndCutListAndHoldsNoPromptText()
    {
        using DocFixture fixture = Ready();

        string reply = Tools(fixture).BuildTaskPrompt("T1");

        Assert.Contains("provenance:", reply, StringComparison.Ordinal);
        Assert.Contains("prompt: built", reply, StringComparison.Ordinal);
        Assert.Contains("agent: ", reply, StringComparison.Ordinal);
        Assert.Contains("difficulty: medium (source: default)", reply, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", reply, StringComparison.Ordinal);
        Assert.Contains("left out: none", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARED-RULES-MARKER", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("CSHARP-RULES-MARKER", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolTakesATechnologyForADispatchThatBelongsToNoTask()
    {
        using DocFixture fixture = Ready();

        string reply = Tools(fixture).BuildTaskPrompt(taskId: null, technology: "csharp");

        Assert.Contains("prompt: built", reply, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("CSHARP-SKILLS-MARKER", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleTaskFileYieldsNoPromptAndTheRepairCommand()
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

        string reply = Tools(fixture).BuildTaskPrompt("T1");

        Assert.Contains("prompt: none", reply, StringComparison.Ordinal);
        Assert.Contains("docs tasks " + PlanTaskFileProbe.PlanPath + " --write", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandPrintsThePromptTheServiceBuilds()
    {
        using DocFixture fixture = Ready();
        KyberWeaveConfig config = Config(fixture);

        string printed = Packet(fixture, "T1").TrimEnd('\r', '\n');
        TaskPromptResult built = Service(fixture).Build(new TaskPromptRequest(fixture.Root, TaskId: "T1"), config);

        Assert.True(built.Built, built.Refusal);

        // The packet prints the service's prompt unchanged, then the task's test command after a blank line.
        Assert.StartsWith(built.Prompt + "\n\n## Test command\n", printed, StringComparison.Ordinal);
    }
}
