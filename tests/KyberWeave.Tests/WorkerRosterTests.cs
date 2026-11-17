using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// The host chooses a worker roster (<c>technology</c> or <c>tiered</c>), an agent may declare one, and the
/// task file records the roster it was written under. Under <c>tiered</c>, a developer task carries one
/// technology; a documentation or delivery task needs none.
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
    public void AnAbsentSettingIsTheTechnologyRoster()
    {
        Assert.Equal(WorkerRosters.Technology, KyberWeaveConfig.ProductDefaults.WorkerRoster);
        Assert.Equal(
            WorkerRosters.Technology,
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
    public void AnotherValueIsAConfigurationErrorNamingBoth()
    {
        YamlException error = Assert.Throws<YamlException>(() =>
            KyberWeaveConfigLoader.LoadFromYaml("worker-roster: side-by-side\n"));

        Assert.Contains("technology", error.Message, StringComparison.Ordinal);
        Assert.Contains("tiered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFourteenRemovedAgentsDeclareTheTechnologyRosterAndTheConductorServesBoth()
    {
        foreach (string name in RemovedAgents)
        {
            string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "agents", name + ".md");
            string text = File.ReadAllText(path);
            Assert.Equal(1, text.Split("worker-roster: technology", StringSplitOptions.None).Length - 1);
        }

        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        Assert.Equal(WorkerRosters.Technology, catalog.Agents["csharp-dev"].WorkerRoster);
        Assert.Null(catalog.Agents["conductor"].WorkerRoster);
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
        string text = PlanTaskFileProbe.TaskFile().Replace(
            "  - id: T1\n",
            "  - id: T1\n    worker-roster: tiered\n",
            StringComparison.Ordinal);
        // The roster is a file-level field; put it at the top for the reader.
        text = text.Replace("    worker-roster: tiered\n", string.Empty, StringComparison.Ordinal)
            .Replace("schema-version:", "worker-roster: tiered\nschema-version:", StringComparison.Ordinal);
        fixture.Write(PlanTaskFileProbe.TaskFilePath, text);

        DiagnosticReport report = ValidateTiered(fixture);

        Assert.Contains(report.Items, item => item.Code == PlanTaskFileRules.MissingTechnologyUnderTiered);
    }

    [Fact]
    public void UnderTieredADeveloperTaskWithTechnologyIsAccepted()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string text = AddToEachTask(PlanTaskFileProbe.TaskFile(), "    technology: csharp")
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

    [Fact]
    public void UnderTheTechnologyRosterNoTechnologyIsRequired()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());

        DiagnosticReport report = new PlanTaskFileValidator(
            fixture.Root,
            KyberWeaveConfig.ProductDefaults,
            DocsPorts.Files).Validate(fixture.Load());

        Assert.DoesNotContain(report.Items, item => item.Code == PlanTaskFileRules.MissingTechnologyUnderTiered);
    }

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
                .WithOntology(new OntologyConfig { Technologies = ["csharp"] })
                .WithWorkerRoster(WorkerRosters.Tiered),
            DocsPorts.Files).Validate(fixture.Load());
}
