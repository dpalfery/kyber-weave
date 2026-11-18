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
using YamlDotNet.Core;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// What the prompt budget cuts, and in what order: rules stay whole, skills go before standard sections,
/// the least relevant goes first, and every cut is listed in the last part. The shipped budget fits every
/// shipped template with its parent and has room for the task and host-facts parts a task prompt also carries.
/// </summary>
public sealed class TaskPromptBudgetTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>A task prompt also carries these two parts, each capped at 6,000 characters by the packet.</summary>
    private const int TaskAndHostFactsBudget = 12000;

    private const string ConfigYaml = """
        ontology:
          docs-root: 6-Docs
          technologies: [csharp]
        task-prompt-budget: {budget}
        """;

    private static KyberWeaveConfig Config(int? budget = null) =>
        KyberWeaveConfigLoader.LoadFromYaml(ConfigYaml.Replace(
            "{budget}",
            (budget ?? KyberWeaveConfig.DefaultTaskPromptBudget).ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal));

    /// <summary>
    /// A budget that is not a positive whole number of characters is refused with the key and the shipped value,
    /// rather than accepted as zero (no prompt) or silently replaced.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void ABudgetThatIsNotAPositiveWholeNumberIsRefused(string budget)
    {
        string yaml = ConfigYaml.Replace("{budget}", budget, StringComparison.Ordinal);

        Assert.Throws<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml(yaml));
    }

    /// <summary>A corpus whose rules are small and whose skills and standard sections are large.</summary>
    private static DocFixture Ready()
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(BudgetPlan());
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

    /// <summary>The fixture plan with a technology, and a task whose text names the naming and layout section.</summary>
    private static string BudgetPlan() =>
        PlanTaskFileProbe.PlanMarkdown()
            .Replace(
                "### T1: The task file validator",
                "### T1: The naming and layout standard for csharp",
                StringComparison.Ordinal)
            .Replace(
                "- **Depends on:** none",
                "- **Depends on:** none\n- **Technology:** csharp",
                StringComparison.Ordinal);

    private static string Filler(int length)
    {
        const string Sentence = "The section carries detailed reference prose that no task names. ";
        return string.Concat(Enumerable.Repeat(Sentence, (length / Sentence.Length) + 1))[..length];
    }

    private static void Standards(DocFixture fixture)
    {
        fixture.Write("6-Docs/standards/shared-rules.md", Document(
            "standards/shared-rules", "Shared rules", "rule",
            "# Shared rules\n\nSHARED-RULES-MARKER: these hold for every technology.",
            null));
        fixture.Write("6-Docs/standards/csharp/csharp.rules.md", Document(
            "standards/csharp/rules", "csharp rules", "rule",
            "# csharp rules\n\nCSHARP-RULES-MARKER: nullable is on.",
            "csharp"));
        fixture.Write("6-Docs/standards/csharp/csharp.skills.md", Document(
            "standards/csharp/skills", "csharp skills", "skill",
            "# csharp skills\n\nCSHARP-SKILLS-MARKER: run dotnet format.\n\n" + Filler(1200),
            "csharp"));
        fixture.Write("6-Docs/standards/csharp/csharp.standards.md", Document(
            "standards/csharp", "C# coding standard", "coding-standard",
            "# C# coding standard\n\n"
            + "## Naming and layout\n\nSTANDARD-NAMING-MARKER: names use PascalCase.\n\n"
            + "## Testing\n\nSTANDARD-TESTING-MARKER: " + Filler(2000) + "\n\n"
            + "## Security and data handling\n\nSTANDARD-SECURITY-MARKER: " + Filler(2000),
            "csharp"));
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

    private static TaskPromptResult Build(DocFixture fixture, KyberWeaveConfig config, string? retryText = null) =>
        new TaskPromptService(
            DocsTestCommands.Services().GetRequiredService<IPlanTaskService>(),
            DocsTestCommands.Services().GetRequiredService<IPacketHostFacts>(),
            DocsTestCommands.Services().GetRequiredService<IFileSystem>(),
            Index(fixture, config).StandardsFor)
        .Build(new TaskPromptRequest(fixture.Root, TaskId: "T1", RetryText: retryText), config);

    private static DocumentIndex Index(DocFixture fixture, KyberWeaveConfig config) =>
        DocumentIndex.Build(new DocumentLoader(fixture.Root, config.Ontology).Load(), FakeCodeGraphResolver.WithSymbols());

    [Fact]
    public void EveryPartFitsTheDefaultBudget()
    {
        using DocFixture fixture = Ready();
        KyberWeaveConfig config = Config();
        TaskPromptResult full = Build(fixture, config);

        Assert.True(full.Built, full.Refusal);
        Assert.Empty(full.LeftOut);
        Assert.Contains("CSHARP-SKILLS-MARKER", full.Prompt, StringComparison.Ordinal);
        Assert.Contains("STANDARD-TESTING-MARKER", full.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OverTheBudgetSkillsAreCutFirstAndTheCutIsListedInTheLastPart()
    {
        using DocFixture fixture = Ready();
        int full = Build(fixture, Config()).Prompt.Length;
        KyberWeaveConfig tight = Config(full - 100);

        TaskPromptResult cut = Build(fixture, tight);

        Assert.True(cut.Built, cut.Refusal);
        Assert.True(cut.Prompt.Length <= tight.TaskPromptBudget, $"the cut prompt is {cut.Prompt.Length} characters");
        string leftOut = cut.Prompt[cut.Prompt.IndexOf("<left-out>", StringComparison.Ordinal)..];
        Assert.Contains("skills", leftOut, StringComparison.Ordinal);
        Assert.DoesNotContain("standard section", leftOut, StringComparison.Ordinal);
        Assert.DoesNotContain("CSHARP-SKILLS-MARKER", cut.Prompt, StringComparison.Ordinal);
        Assert.Contains("STANDARD-TESTING-MARKER", cut.Prompt, StringComparison.Ordinal);
        Assert.Contains("SHARED-RULES-MARKER", cut.Prompt, StringComparison.Ordinal);
        Assert.Contains("CSHARP-RULES-MARKER", cut.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OverTheBudgetTheLeastRelevantStandardSectionGoesLastAndRulesStayWhole()
    {
        using DocFixture fixture = Ready();
        int full = Build(fixture, Config()).Prompt.Length;
        KyberWeaveConfig tight = Config(full - 1350);

        TaskPromptResult cut = Build(fixture, tight);

        Assert.True(cut.Built, cut.Refusal);
        string leftOut = cut.Prompt[cut.Prompt.IndexOf("<left-out>", StringComparison.Ordinal)..];
        Assert.Contains("skills", leftOut, StringComparison.Ordinal);
        Assert.Contains("standard section", leftOut, StringComparison.Ordinal);
        Assert.DoesNotContain("Naming and layout", leftOut, StringComparison.Ordinal);
        Assert.Contains("STANDARD-NAMING-MARKER", cut.Prompt, StringComparison.Ordinal);
        Assert.Contains("SHARED-RULES-MARKER", cut.Prompt, StringComparison.Ordinal);
        Assert.Contains("CSHARP-RULES-MARKER", cut.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prompt can still exceed the budget after every cut is used: the parts that stay whole are the ones a worker
    /// cannot recover. The last part must say so rather than return silently.
    /// </summary>
    [Fact]
    public void APromptStillOverTheBudgetAfterEveryCutSaysSoInTheLastPart()
    {
        using DocFixture fixture = Ready();
        KyberWeaveConfig tiny = Config(100);

        TaskPromptResult result = Build(fixture, tiny);

        Assert.True(result.Built, result.Refusal);
        string leftOut = result.Prompt[result.Prompt.IndexOf("<left-out>", StringComparison.Ordinal)..];
        Assert.Contains("still over the 100-character prompt budget", leftOut, StringComparison.Ordinal);
    }

    /// <summary>A retry's failure report rides in the same dispatch, so the prompt leaves it room rather than overshooting.</summary>
    [Fact]
    public void TheRetryTextIsBudgetedWithThePrompt()
    {
        using DocFixture fixture = Ready();
        KyberWeaveConfig budget = Config(Build(fixture, Config()).Prompt.Length + 50);

        TaskPromptResult plain = Build(fixture, budget);
        TaskPromptResult retry = Build(fixture, budget, retryText: new string('x', 400));

        Assert.True(plain.Built, plain.Refusal);
        Assert.Empty(plain.LeftOut);
        Assert.True(retry.Built, retry.Refusal);
        Assert.NotEmpty(retry.LeftOut);
    }

    [Fact]
    public void EveryShippedTemplateWithItsParentFitsTheDefaultBudgetWithoutACut()
    {
        const string Templates = "products/kyber-squad/standards";
        List<string> technologies =
        [
            .. Corpus.EnumerateDirectories(Templates)
                .Select(folder => Path.GetFileName(folder))
                .Where(name => Corpus.Exists($"{Templates}/{name}/{name}.standards.md"))
                .Order(StringComparer.Ordinal)
        ];
        Assert.NotEmpty(technologies);

        using DocFixture fixture = new DocFixture();
        foreach (string file in Corpus.EnumerateFiles(Templates, "*.md"))
        {
            string relative = file[(Templates.Length + 1)..];
            fixture.Write("6-Docs/standards/" + relative, Corpus.ReadText(file));
        }

        KyberWeaveConfig config = Config();
        KyberWeaveConfig site = KyberWeaveConfigLoader.LoadFromYaml("""
            ontology:
              docs-root: 6-Docs
            """);
        DocumentIndex index = DocumentIndex.Build(
            new DocumentLoader(fixture.Root, site.Ontology).Load(), FakeCodeGraphResolver.WithSymbols());
        TaskPromptService service = new(
            DocsTestCommands.Services().GetRequiredService<IPlanTaskService>(),
            DocsTestCommands.Services().GetRequiredService<IPacketHostFacts>(),
            DocsTestCommands.Services().GetRequiredService<IFileSystem>(),
            index.StandardsFor);

        foreach (string technology in technologies)
        {
            TaskPromptResult result = service.Build(
                new TaskPromptRequest(fixture.Root, TaskId: null, Technology: technology),
                config);

            Assert.True(result.Built, technology + ": " + result.Refusal);
            Assert.True(
                result.LeftOut.Count == 0,
                $"{technology} left out: {string.Join("; ", result.LeftOut)}");
            Assert.True(
                result.Prompt.Length + TaskAndHostFactsBudget <= KyberWeaveConfig.DefaultTaskPromptBudget,
                $"{technology} needs {result.Prompt.Length + TaskAndHostFactsBudget} characters of the "
                + $"{KyberWeaveConfig.DefaultTaskPromptBudget}-character budget once its task and host facts are added");
        }
    }
}
