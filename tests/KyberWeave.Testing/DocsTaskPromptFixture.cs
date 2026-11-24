using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;

namespace KyberWeave.Tests;

/// <summary>
/// The ready plan, its standards and the task-prompt collaborators over an in-memory checkout, for the unit tests of
/// the task-prompt tool and of the packet command that prints the same prompt.
/// </summary>
internal static class DocsTaskPromptFixture
{
    /// <summary>A ready plan with its task file written, on an in-memory checkout.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The caller owns the fixture it is returned.")]
    internal static DocFixture Ready(string plan = "")
    {
        DocFixture fixture = PlanTaskFileProbe.CorpusInMemory(plan.Length == 0 ? PlanNaming() : plan);
        Standards(fixture);
        _ = fixture.EnsureDirectory(".git");
        fixture.Write(".kyber-weave/kyber-weave.yml", ConfigYaml);
        using TestConsole console = new() { EmitAnsiSequences = false };
        _ = DocsTestCommands.Tasks(Services(fixture), console).Execute(
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

    private static IServiceProvider Services(DocFixture fixture) => DocsMemoryServicesD.Services(fixture);

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

    internal static KyberWeaveConfig Config(DocFixture fixture) =>
        Services(fixture).GetRequiredService<IKyberWeaveConfigRepository>().Load(fixture.Root);

    private static DocumentIndexHost Host(DocFixture fixture, KyberWeaveConfig config) => new(
        fixture.Root,
        () => FakeCodeGraphResolver.WithSymbols(),
        () => fixture.Loader(config.Ontology).Load(),
        fixture.Store,
        fixture.Paths,
        fixture.Environment,
        config.Ontology.DocsRoots,
        config.Ontology.ResolvedCatalogPath);

    internal static DocsTools Tools(DocFixture fixture) =>
        DocsToolsComposition.Create(Host(fixture, Config(fixture)), services: Services(fixture));

    internal static TaskPromptService Service(DocFixture fixture, IKwSkillCatalog? skills = null)
    {
        IServiceProvider provider = Services(fixture);
        KyberWeaveConfig config = Config(fixture);
        return new TaskPromptService(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IWritableFileSystem>(),
            Host(fixture, config).Current().StandardsFor,
            provider.GetRequiredService<IProcessEnvironment>(),
            skills ?? FixedKwSkillCatalog.Empty);
    }

    /// <summary>What <c>docs packet</c> prints for <paramref name="task"/>.</summary>
    internal static string Packet(DocFixture fixture, string task)
    {
        DocsPacketSettings settings = new()
        {
            Task = task,
            WorkingDirectory = fixture.Root,
            ListWorktrees = _ => [fixture.Root]
        };
        using TestConsole console = new() { EmitAnsiSequences = false };
        _ = DocsTestCommands.Packet(Services(fixture), console).Execute(null!, settings, CancellationToken.None);
        return console.Output;
    }
}

/// <summary>
/// An in-memory KW skill catalog whose lists a test states directly, so a prompt test states which skills a
/// technology and a role agent get without a checkout's skill pool.
/// </summary>
/// <param name="skillsFor">Answers the list for a technology and a role agent, as the real catalog does.</param>
internal sealed class FixedKwSkillCatalog(Func<string?, string?, KwSkillList> skillsFor) : IKwSkillCatalog
{
    /// <summary>A catalog that lists no skill for any technology or role.</summary>
    internal static FixedKwSkillCatalog Empty { get; } = new((_, _) => new KwSkillList([], []));

    /// <inheritdoc />
    public KwSkillList SkillsFor(string? technology, string? roleAgent) => skillsFor(technology, roleAgent);

    /// <inheritdoc />
    public KwSkillLoadResult Load(string name) => KwSkillLoadResult.Unknown(name, Names());

    /// <inheritdoc />
    public IReadOnlyList<string> Names() => [];
}
