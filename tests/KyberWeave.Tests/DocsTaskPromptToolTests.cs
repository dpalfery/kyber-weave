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
/// Disk-backed helpers that classes still in this project use (<c>TaskPromptFileFallbackTests</c>). The test cases
/// moved to <c>KyberWeave.Mcp.Tests</c> and <c>KyberWeave.Cli.Tests</c> over <c>DocsTaskPromptFixture</c>; delete this
/// class when the classes that call it move to in-memory fixtures.
/// </summary>
internal static class DocsTaskPromptToolTests
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
            provider.GetRequiredService<IWritableFileSystem>(),
            Host(fixture, config).Current().StandardsFor,
            provider.GetRequiredService<IProcessEnvironment>());
    }

    internal static string Packet(DocFixture fixture, string task)
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
}
