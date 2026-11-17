using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// The removal leaves one roster: the three developer agents by difficulty. An absent setting is
/// <c>tiered</c>, the value <c>technology</c> is a configuration error that names the change, an agent
/// may declare a roster, and the task file records the roster it was written under. Under <c>tiered</c> a
/// developer task carries one technology; a documentation or delivery task needs none.
/// </summary>
public sealed class WorkerRosterTests
{
    private static readonly string[] RemovedAgents =
    [
        "azure-reader", "csharp-dev", "dal-dev", "github-devops", "go-dev", "maui-dev", "pulumi-dev",
        "python-dev", "react-dev", "rust-dev", "sql-database-architect", "tauri-dev", "test-dev",
        "typescript-dev"
    ];

    [Fact]
    public void AnAbsentSettingIsTheTieredRoster()
    {
        Assert.Equal(WorkerRosters.Tiered, KyberWeaveConfig.ProductDefaults.WorkerRoster);
        Assert.Equal(
            WorkerRosters.Tiered,
            KyberWeaveConfigLoader.LoadFromYaml("ontology:\n  docs-root: docs\n").WorkerRoster);
    }

    [Fact]
    public void TheTieredRosterLoads()
    {
        Assert.Equal(
            WorkerRosters.Tiered,
            KyberWeaveConfigLoader.LoadFromYaml("worker-roster: tiered\n").WorkerRoster);
    }

    [Fact]
    public void TheTechnologyRosterIsAnErrorThatNamesTheChange()
    {
        YamlException error = Assert.Throws<YamlException>(() =>
            KyberWeaveConfigLoader.LoadFromYaml("worker-roster: technology\n"));

        Assert.Contains("worker-roster", error.Message, StringComparison.Ordinal);
        Assert.Contains("technology", error.Message, StringComparison.Ordinal);
        Assert.Contains("tiered", error.Message, StringComparison.Ordinal);
        Assert.Contains("removed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnotherValueIsAConfigurationErrorNamingTheRoster()
    {
        YamlException error = Assert.Throws<YamlException>(() =>
            KyberWeaveConfigLoader.LoadFromYaml("worker-roster: side-by-side\n"));

        Assert.Contains("tiered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedSquadHoldsFifteenAgentsAndNoRemovedAgentBody()
    {
        string[] agentFiles = Directory
            .EnumerateFiles(
                Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "agents"),
                "*.md",
                SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(15, agentFiles.Length);
        Assert.Empty(agentFiles.Intersect(RemovedAgents, StringComparer.Ordinal));

        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        Assert.Equal(agentFiles, catalog.Agents.Keys.Order(StringComparer.Ordinal));
        Assert.All(
            catalog.Agents.Values,
            agent => Assert.True(
                agent.WorkerRoster is null or WorkerRosters.Tiered,
                $"Agent '{agent.Name}' declares worker-roster '{agent.WorkerRoster}'; only the tiered roster remains."));
        Assert.Null(catalog.Agents["conductor"].WorkerRoster);
        Assert.Equal(WorkerRosters.Tiered, catalog.Agents["dev-medium"].WorkerRoster);
    }

    [Fact]
    public void TheTaskFileRecordsTheRosterItWasWrittenUnder()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        KyberWeaveConfig tiered = KyberWeaveConfig.ProductDefaults.WithWorkerRoster(WorkerRosters.Tiered);

        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(fixture.Root, tiered, DocsPorts.Files)
            .Run(PlanTaskFileProbe.PlanPath, PlanTaskFileMode.Write);

        Assert.Equal(WorkerRosters.Tiered, outcome.File!.WorkerRoster);
    }

    [Fact]
    public void UnderTieredADeveloperTaskWithoutTechnologyIsAnError()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string text = NamedDeveloper(PlanTaskFileProbe.TaskFile())
            .Replace("schema-version:", "worker-roster: tiered\nschema-version:", StringComparison.Ordinal);
        fixture.Write(PlanTaskFileProbe.TaskFilePath, text);

        DiagnosticReport report = ValidateTiered(fixture);

        Assert.Contains(report.Items, item => item.Code == PlanTaskFileRules.MissingTechnologyUnderTiered);
    }

    [Fact]
    public void UnderTieredADeveloperTaskWithTechnologyIsAccepted()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string text = AddToEachTask(NamedDeveloper(PlanTaskFileProbe.TaskFile()), "    technology: csharp")
            .Replace("schema-version:", "worker-roster: tiered\nschema-version:", StringComparison.Ordinal);
        fixture.Write(PlanTaskFileProbe.TaskFilePath, text);

        DiagnosticReport report = ValidateTiered(fixture);

        Assert.DoesNotContain(report.Items, item => item.Code == PlanTaskFileRules.MissingTechnologyUnderTiered);
    }

    [Fact]
    public void UnderTieredADocumentationTaskNeedsNoTechnology()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string text = PlanTaskFileProbe.TaskFile()
            .Replace("role: csharp-dev", "role: docs-dev", StringComparison.Ordinal)
            .Replace("role: test-dev", "role: docs-dev", StringComparison.Ordinal)
            .Replace("schema-version:", "worker-roster: tiered\nschema-version:", StringComparison.Ordinal);
        fixture.Write(PlanTaskFileProbe.TaskFilePath, text);

        DiagnosticReport report = ValidateTiered(fixture);

        Assert.DoesNotContain(report.Items, item => item.Code == PlanTaskFileRules.MissingTechnologyUnderTiered);
    }

    /// <summary>
    /// A task file written before the removal records the roster it was written under. That roster is
    /// gone, so the file is read as <c>tiered</c> and reported as a warning naming the change, rather than
    /// dispatched against agents that no longer ship. Its tasks keep validating: the technology requirement
    /// belongs to a file written under the roster that has it.
    /// </summary>
    [Fact]
    public void ATaskFileThatRecordsTheTechnologyRosterIsReadAsTieredAndWarns()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string text = PlanTaskFileProbe.TaskFile()
            .Replace("schema-version:", "worker-roster: technology\nschema-version:", StringComparison.Ordinal);
        fixture.Write(PlanTaskFileProbe.TaskFilePath, text);

        DiagnosticReport report = ValidateTiered(fixture);

        Diagnostic warning = Assert.Single(
            report.Items,
            item => item.Code == PlanTaskFileRules.RetiredTechnologyRoster);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("tiered", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            report.Items,
            item => item.Code == PlanTaskFileRules.MissingTechnologyUnderTiered);
    }

    /// <summary>
    /// The probe's task file with each task's worker named as the shipped developer tier it would dispatch to.
    /// A removed agent's name resolves at dispatch time, so it is the shipped name that carries the requirement.
    /// </summary>
    private static string NamedDeveloper(string yaml) => yaml
        .Replace("role: csharp-dev", "role: dev-medium", StringComparison.Ordinal)
        .Replace("role: test-dev", "role: dev-medium", StringComparison.Ordinal);

    private static string AddToEachTask(string yaml, string line)
    {
        List<string> edited = [];
        foreach (string raw in yaml.Split('\n'))
        {
            edited.Add(raw);
            if (raw.TrimStart().StartsWith("- id: ", StringComparison.Ordinal))
            {
                edited.Add(line);
            }
        }

        return string.Join('\n', edited);
    }

    private static DiagnosticReport ValidateTiered(DocFixture fixture) =>
        new PlanTaskFileValidator(
            fixture.Root,
            KyberWeaveConfig.ProductDefaults
                .WithOntology(new OntologyConfig { Technologies = ["csharp"] }),
            DocsPorts.Files).Validate(fixture.Load());
}
