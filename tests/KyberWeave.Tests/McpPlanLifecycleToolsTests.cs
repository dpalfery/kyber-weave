using System.Diagnostics;
using System.Reflection;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The docs server's two plan lifecycle tools, which the conductor calls instead of dispatching a model: finalize an
/// approved Draft plan, and archive a plan whose closeout evidence is complete. Each refusal changes nothing, and the
/// archive refusal sends its findings to docs-dev.
/// </summary>
public sealed class McpPlanLifecycleToolsTests
{
    private const string ClosedPlan = "6-Docs/plans/2026-10-20-widget.md";
    private const string ArchivedPlan = "6-Docs/archive/plans/2026-10-20-widget.md";

    private static DocsTools Tools(string root) =>
        DocsRootRegistry.CreateTools(root, codeGraphRefreshEnabled: false,
            new DocsRootRegistry(root, path => DocsRootRegistry.CreateTools(path, codeGraphRefreshEnabled: false, registry: null)));

    private static DocFixture GitRepository(DocFixture fixture)
    {
        string git = Path.Combine(fixture.Root, ".git");
        if (Directory.Exists(git))
            Directory.Delete(git, recursive: true);
        Git(fixture.Root, "init", "-q", "-b", "main");
        return fixture;
    }

    private static string Read(DocFixture fixture, string relative) =>
        File.ReadAllText(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(DocFixture fixture, string relative) =>
        File.Exists(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData(DocsTools.FinalizeToolName)]
    [InlineData(DocsTools.ArchiveToolName)]
    public void TheLifecycleToolsChangeFilesInAClosedWorldAndRequireAProjectPath(string toolName)
    {
        MethodInfo method = typeof(DocsTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.False(attribute.ReadOnly);
        Assert.False(attribute.OpenWorld);
        Assert.False(attribute.Destructive);
        ParameterInfo projectPath = method.GetParameters()[0];
        Assert.Equal("projectPath", projectPath.Name);
        Assert.False(projectPath.HasDefaultValue);
    }

    [Fact]
    public void FinalizeSetsTheDraftPlanReadyAndNamesTheTaskFile()
    {
        using DocFixture fixture = GitRepository(PlanFinalizeTests.Repository());

        string response = Tools(fixture.Root).FinalizePlan(fixture.Root, PlanTaskFileProbe.PlanPath, "autonomous",
            "AUTONOMOUS_APPROVAL: approve-and-execute");

        Assert.Contains("result: finalized", response, StringComparison.Ordinal);
        Assert.Contains($"TASK_FILE: {PlanTaskFileProbe.TaskFilePath}", response, StringComparison.Ordinal);
        Assert.True(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.Contains("**Status: Ready,", Read(fixture, PlanTaskFileProbe.PlanPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "autonomous")]
    [InlineData("ROOT", "approve")]
    public void FinalizeWithoutAProjectPathOrAModeIsRefusedAndChangesNothing(string projectPath, string mode)
    {
        using DocFixture fixture = GitRepository(PlanFinalizeTests.Repository());
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);

        string response = Tools(fixture.Root).FinalizePlan(projectPath == "ROOT" ? fixture.Root : projectPath,
            PlanTaskFileProbe.PlanPath, mode, "AUTONOMOUS_APPROVAL: approve-and-execute");

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Fact]
    public void ArchiveRefusesAPlanWithoutCloseoutEvidenceAndSendsTheFindingsToDocsDev()
    {
        using DocFixture fixture = GitRepository(PlanFinalizeTests.Repository());
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);

        string response = Tools(fixture.Root).ArchivePlan(fixture.Root, PlanTaskFileProbe.PlanPath);

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.Contains("KW-DOC-LIFECYCLE-004", response, StringComparison.Ordinal);
        Assert.Contains("send these findings to docs-dev", response, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
    }

    [Fact]
    public void ArchiveMovesAPlanWhoseCloseoutEvidenceIsComplete()
    {
        using DocFixture fixture = GitRepository(new DocFixture().WithCatalog()
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(ClosedPlan, CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready"))
            .Write("6-Docs/plans/README.md", """
                ---
                id: plans/index
                title: Plan Inventory
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-11-01
                ---

                # Plan Inventory

                | Plan | Status |
                |---|---|
                | [2026-10-20-widget.md](2026-10-20-widget.md) | Ready |
                """));

        string response = Tools(fixture.Root).ArchivePlan(fixture.Root, "2026-10-20-widget");

        Assert.Contains("result: archived", response, StringComparison.Ordinal);
        Assert.Contains("mergeReady true", response, StringComparison.Ordinal);
        Assert.False(Exists(fixture, ClosedPlan));
        Assert.True(Exists(fixture, ArchivedPlan));
    }

    [Fact]
    public void ArchiveRefusesAPathThatIsNotAPlan()
    {
        using DocFixture fixture = GitRepository(PlanFinalizeTests.Repository());

        string response = Tools(fixture.Root).ArchivePlan(fixture.Root, "src/Placeholder.cs");

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.True(Exists(fixture, "src/Placeholder.cs"));
    }

    private static void Git(string directory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".no-global-gitconfig");
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}
